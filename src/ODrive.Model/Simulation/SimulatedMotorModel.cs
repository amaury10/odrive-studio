using System.Diagnostics;
using ODrive.Protocol;
using ODrive.Protocol.Simulation;

namespace ODrive.Model.Simulation;

/// <summary>Moteur et capteur réellement « branchés » sur un axe simulé (ce que les calibrations vont mesurer).</summary>
/// <param name="PolePairs">Paires de pôles réelles ; une roue d'hoverboard en a 15.</param>
/// <param name="PhaseResistance">Résistance de phase, en ohms (guide hoverboard : ≈ 0,18 Ω).</param>
/// <param name="PhaseInductance">Inductance de phase, en henrys (guide hoverboard : ≈ 0,34 mH).</param>
/// <param name="ResponseTime">Constante de temps de la vitesse en boucle fermée, en secondes.</param>
public sealed record SimulatedMotorOptions(
    int PolePairs = 15,
    float PhaseResistance = 0.18f,
    float PhaseInductance = 0.00034f,
    double ResponseTime = 0.08)
{
    /// <summary>Durées des calibrations, en secondes (ordre de grandeur de la carte réelle).</summary>
    public double MotorCalibrationTime { get; init; } = 4;
    public double HallPolarityCalibrationTime { get; init; } = 3;
    public double EncoderOffsetCalibrationTime { get; init; } = 6;

    /// <summary>
    /// Mécanique réelle de l'axe. Absente : la vitesse suit sa consigne avec le retard <see cref="ResponseTime"/>
    /// (assez pour l'assistant et le jog). Présente : inertie et frottements, pilotés par la reproduction des
    /// boucles position → vitesse → couple du firmware avec les gains de la carte (pour l'autotune).
    /// </summary>
    public MechanicalLoad? Mechanics { get; init; }
}

/// <summary>Charge mécanique d'un axe simulé, en unités firmware (tours).</summary>
/// <param name="Inertia">J, en Nm/(tour/s²).</param>
/// <param name="Viscous">Frottement visqueux, en Nm/(tour/s).</param>
/// <param name="Coulomb">Frottement sec, en Nm.</param>
/// <param name="VelocityNoise">Écart type du bruit de <c>encoder.vel_estimate</c>, en tours/s.</param>
public sealed record MechanicalLoad(double Inertia, double Viscous = 0, double Coulomb = 0, double VelocityNoise = 0);

/// <summary>
/// Modèle moteur simple greffé sur <see cref="SimulatedODrive"/> : machine d'états des axes avec les
/// calibrations, leurs conditions et leurs erreurs (d'après <c>ref/ODrive/Firmware/MotorControl/axis.cpp</c>),
/// boucle fermée en vitesse, position ou couple, watchdog. Le temps avance à chaque requête reçue : sans
/// requête il ne se passe rien, ce qui rend les tests déterministes avec une horloge fournie.
/// <para>Ce n'est pas une simulation électrique : juste assez pour dérouler l'assistant, le jog et les tests.</para>
/// </summary>
public sealed class SimulatedMotorModel
{
    private readonly SimulatedODrive _board;
    private readonly Func<TimeSpan> _clock;
    private readonly List<Axis> _axes = [];
    private TimeSpan _last;

    private SimulatedMotorModel(SimulatedODrive board, SimulatedMotorOptions options, Func<TimeSpan> clock)
    {
        _board = board;
        _clock = clock;
        _last = clock();
        foreach (ObjectNode node in board.Schema.Root.Members.OfType<ObjectNode>())
        {
            if (board.Schema.FindProperty(node.Path + ".requested_state") is not null
                && board.Schema.FindProperty(node.Path + ".motor.is_calibrated") is not null)
            {
                var axis = new Axis(board, node.Path + ".", options);
                _axes.Add(axis);
                board.OnWrite(node.Path + ".requested_state", _ => axis.Request(_clock()));
                board.OnCall(node.Path + ".watchdog_feed", _ => axis.FeedWatchdog(_clock()));
                axis.Boot(_clock());
            }
        }

        board.BeforeRequest += Step;
        board.Rebooted += (_, _) =>
        {
            foreach (Axis axis in _axes)
            {
                axis.Boot(_clock());
            }
        };
    }

    /// <summary>Greffe le modèle sur tous les axes de la carte.</summary>
    /// <param name="clock">Horloge ; par défaut le temps réel. Les tests fournissent la leur.</param>
    public static SimulatedMotorModel Attach(SimulatedODrive board, SimulatedMotorOptions? options = null,
        Func<TimeSpan>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (clock is null)
        {
            var watch = Stopwatch.StartNew();
            clock = () => watch.Elapsed;
        }
        return new SimulatedMotorModel(board, options ?? new SimulatedMotorOptions(), clock);
    }

    private void Step()
    {
        TimeSpan now = _clock();
        double dt = Math.Clamp((now - _last).TotalSeconds, 0, 1);
        _last = now;
        foreach (Axis axis in _axes)
        {
            axis.Step(now, dt);
        }
    }

    private sealed class Axis(SimulatedODrive board, string prefix, SimulatedMotorOptions motor)
    {
        private readonly Queue<AxisState> _chain = new();
        private AxisState _state = AxisState.Idle;
        private TimeSpan _stateEnd;
        private TimeSpan _watchdogFed;
        private double _velocity;
        private double _position;
        private double _velocitySetpoint;
        private double _integrator;
        private readonly Random _noise = new(1);

        public void Boot(TimeSpan now)
        {
            _chain.Clear();
            _state = AxisState.Idle;
            _velocity = _position = _velocitySetpoint = _integrator = 0;
            _watchdogFed = now;
            // motor.cpp:306 et encoder.cpp:26-30 : au démarrage, calibré = pre_calibrated.
            Set("motor.is_calibrated", Get<bool>("motor.config.pre_calibrated"));
            Set("encoder.is_ready", Get<bool>("encoder.config.pre_calibrated"));
            Publish();
        }

        public void FeedWatchdog(TimeSpan now) => _watchdogFed = now;

        // axis.cpp:455-490 : la demande est consommée et transformée en chaîne de tâches.
        public void Request(TimeSpan now)
        {
            var requested = (AxisState)Get<byte>("requested_state");
            Set("requested_state", (byte)AxisState.Undefined);
            if (requested == AxisState.Undefined)
            {
                return;
            }

            Set("error", Get<uint>("error") & ~(uint)AxisError.InvalidState); // axis.cpp:487
            _chain.Clear();
            if (requested == AxisState.FullCalibrationSequence)
            {
                // axis.cpp:472-479
                _chain.Enqueue(AxisState.MotorCalibration);
                if (EncoderMode == EncoderMode.Hall)
                {
                    _chain.Enqueue(AxisState.EncoderHallPolarityCalibration);
                }
                _chain.Enqueue(AxisState.EncoderOffsetCalibration);
            }
            else if (requested != AxisState.Idle)
            {
                _chain.Enqueue(requested);
            }
            StartNext(now);
        }

        public void Step(TimeSpan now, double dt)
        {
            switch (_state)
            {
                case AxisState.MotorCalibration when now >= _stateEnd:
                    FinishMotorCalibration();
                    StartNext(now);
                    break;
                case AxisState.EncoderHallPolarityCalibration when now >= _stateEnd:
                    Set("encoder.config.hall_polarity", (byte)0);
                    Set("encoder.config.hall_polarity_calibrated", true); // encoder.cpp:265
                    StartNext(now);
                    break;
                case AxisState.EncoderOffsetCalibration:
                    // Le rotor fait un aller-retour pendant la mesure.
                    _velocity = now < _stateEnd - TimeSpan.FromSeconds(motor.EncoderOffsetCalibrationTime / 2) ? 0.5 : -0.5;
                    _position += _velocity * dt;
                    if (now >= _stateEnd)
                    {
                        _velocity = 0;
                        FinishOffsetCalibration();
                        StartNext(now);
                    }
                    break;
                case AxisState.ClosedLoopControl:
                    RunClosedLoop(now, dt);
                    break;
                case AxisState.Idle when motor.Mechanics is { } load:
                    for (double left = dt; left > 0; left -= PhysicsStep)
                    {
                        Integrate(load, 0, Math.Min(PhysicsStep, left));
                    }
                    break;
                case AxisState.Idle:
                    _velocity *= Math.Exp(-dt / 0.3); // roue libre : ralentit par frottement
                    _position += _velocity * dt;
                    break;
            }
            Publish();
        }

        private EncoderMode EncoderMode => (EncoderMode)Get<ushort>("encoder.config.mode");

        private void StartNext(TimeSpan now)
        {
            if (!_chain.TryDequeue(out AxisState next))
            {
                Enter(AxisState.Idle);
                return;
            }

            switch (next)
            {
                case AxisState.MotorCalibration:
                    _stateEnd = now + TimeSpan.FromSeconds(motor.MotorCalibrationTime);
                    break;

                case AxisState.EncoderHallPolarityCalibration when EncoderMode == EncoderMode.Hall:
                    _stateEnd = now + TimeSpan.FromSeconds(motor.HallPolarityCalibrationTime);
                    break;

                case AxisState.EncoderOffsetCalibration when Get<bool>("motor.is_calibrated"): // axis.cpp:559
                    if (EncoderMode == EncoderMode.Hall && !Get<bool>("encoder.config.hall_polarity_calibrated"))
                    {
                        Fail(AxisError.EncoderFailed, encoder: EncoderError.HallNotCalibratedYet); // encoder.cpp:338
                        return;
                    }
                    _stateEnd = now + TimeSpan.FromSeconds(motor.EncoderOffsetCalibrationTime);
                    break;

                // axis.cpp:575 : moteur calibré et sens de l'encodeur connu.
                case AxisState.ClosedLoopControl when Get<bool>("motor.is_calibrated")
                        && Get<int>("encoder.config.direction") != 0 && Get<bool>("encoder.is_ready"):
                    _watchdogFed = now; // axis.cpp:577
                    _velocitySetpoint = _velocity;
                    _integrator = 0;                         // axis.cpp:294
                    Set("controller.input_pos", (float)_position); // controller.cpp:118-126 : la consigne part de la position actuelle
                    break;

                default:
                    Fail(AxisError.InvalidState); // axis.cpp:588
                    return;
            }
            Enter(next);
        }

        private void FinishMotorCalibration()
        {
            // motor.cpp:437-445 : la tension nécessaire pour faire passer calibration_current ne doit pas
            // dépasser resistance_calib_max_voltage.
            float current = Get<float>("motor.config.calibration_current");
            if (current * motor.PhaseResistance > Get<float>("motor.config.resistance_calib_max_voltage"))
            {
                Fail(AxisError.MotorFailed, motor: MotorError.PhaseResistanceOutOfRange);
                return;
            }
            Set("motor.config.phase_resistance", motor.PhaseResistance);
            Set("motor.config.phase_inductance", motor.PhaseInductance);
            Set("motor.is_calibrated", true); // motor.cpp:509
        }

        private void FinishOffsetCalibration()
        {
            // encoder.cpp:425-431 : le nombre de pas vus sur un tour électrique doit coller à cpr / pole_pairs.
            int expected = EncoderMode == EncoderMode.Hall ? 6 * motor.PolePairs : Get<int>("encoder.config.cpr");
            if (Get<int>("motor.config.pole_pairs") != motor.PolePairs || Get<int>("encoder.config.cpr") != expected)
            {
                Fail(AxisError.EncoderFailed, encoder: EncoderError.CprPolepairsMismatch);
                return;
            }
            Set("encoder.config.direction", 1);
            Set("encoder.config.phase_offset_float", 0.5f); // encoder.cpp:459 : centré sur 0,5 en Hall
            Set("encoder.is_ready", true);                  // encoder.cpp:461
        }

        private void RunClosedLoop(TimeSpan now, double dt)
        {
            // axis.cpp:166-181
            float timeout = Get<float>("config.watchdog_timeout");
            if (Get<bool>("config.enable_watchdog") && timeout > 0 && (now - _watchdogFed).TotalSeconds > timeout)
            {
                Fail(AxisError.WatchdogTimerExpired);
                return;
            }
            if (Get<uint>("error") != 0)
            {
                Enter(AxisState.Idle);
                return;
            }

            if (motor.Mechanics is { } load)
            {
                RunFirmwareLoops(load, dt);
                return;
            }

            double limit = Get<float>("controller.config.vel_limit");
            double target;
            switch ((ControlMode)Get<byte>("controller.config.control_mode"))
            {
                case ControlMode.PositionControl:
                    double error = Get<float>("controller.input_pos") - _position;
                    target = Math.Clamp(error * Get<float>("controller.config.pos_gain"), -limit, limit);
                    break;
                case ControlMode.TorqueControl:
                    // Couple constant : accélération proportionnelle, bornée par la limite de vitesse.
                    target = Math.Clamp(_velocity + Get<float>("controller.input_torque") * 20 * dt, -limit, limit);
                    break;
                default:
                    target = Get<float>("controller.input_vel");
                    if ((InputMode)Get<byte>("controller.config.input_mode") == InputMode.VelRamp)
                    {
                        double step = Get<float>("controller.config.vel_ramp_rate") * dt;
                        target = _velocitySetpoint + Math.Clamp(target - _velocitySetpoint, -step, step);
                    }
                    if (Get<bool>("controller.config.enable_vel_limit"))
                    {
                        target = Math.Clamp(target, -limit, limit);
                    }
                    break;
            }

            _velocitySetpoint = target;
            double previous = _velocity;
            _velocity += (target - _velocity) * (1 - Math.Exp(-dt / motor.ResponseTime));
            _position += _velocity * dt;

            // Courant : frottement visqueux + accélération, ramené au couple par torque_constant.
            float torqueConstant = Math.Max(Get<float>("motor.config.torque_constant"), 1e-3f);
            double acceleration = dt > 0 ? (_velocity - previous) / dt : 0;
            double torque = 0.02 * _velocity + 0.01 * acceleration;
            Set("motor.current_control.Iq_setpoint", (float)(torque / torqueConstant));
            Set("motor.current_control.Iq_measured", (float)(torque / torqueConstant));
        }

        // Pas d'intégration : la boucle de commande du firmware tourne à 8 kHz (current_meas_hz).
        private const double PhysicsStep = 1.0 / 8000;

        // controller.cpp:296-420, sans les modes d'entrée filtrés ni l'anticogging : passthrough et rampe de vitesse.
        private void RunFirmwareLoops(MechanicalLoad load, double dt)
        {
            var mode = (ControlMode)Get<byte>("controller.config.control_mode");
            var inputMode = (InputMode)Get<byte>("controller.config.input_mode");
            double inputPos = Get<float>("controller.input_pos"), inputVel = Get<float>("controller.input_vel");
            double inputTorque = Get<float>("controller.input_torque");
            double posGain = Get<float>("controller.config.pos_gain"), velGain = Get<float>("controller.config.vel_gain");
            double integratorGain = Get<float>("controller.config.vel_integrator_gain");
            double velLimit = Get<float>("controller.config.vel_limit");
            bool enableVelLimit = Get<bool>("controller.config.enable_vel_limit");
            bool torqueModeVelLimit = Get<bool>("controller.config.enable_torque_mode_vel_limit");
            double tolerance = Get<float>("controller.config.vel_limit_tolerance");
            bool overspeedError = Get<bool>("controller.config.enable_overspeed_error");
            double torqueConstant = Math.Max(Get<float>("motor.config.torque_constant"), 1e-3f);
            double torqueLimit = Math.Min(Get<float>("motor.config.current_lim") * torqueConstant,
                Get<float>("motor.config.torque_lim"));
            double rampRate = Get<float>("controller.config.vel_ramp_rate");

            double torque = 0;
            for (double left = dt; left > 0; left -= PhysicsStep)
            {
                double h = Math.Min(PhysicsStep, left);
                double velSetpoint = inputVel;
                if (inputMode == InputMode.VelRamp)
                {
                    velSetpoint = _velocitySetpoint + Math.Clamp(inputVel - _velocitySetpoint, -rampRate * h, rampRate * h);
                }
                _velocitySetpoint = velSetpoint;

                double velDes = velSetpoint;
                if (mode >= ControlMode.PositionControl)
                {
                    velDes += posGain * (inputPos - _position);
                }
                if (enableVelLimit)
                {
                    velDes = Math.Clamp(velDes, -velLimit, velLimit);
                }
                if (overspeedError && Math.Abs(_velocity) > tolerance * velLimit) // controller.cpp:331-341
                {
                    Set("controller.error", (byte)(Get<byte>("controller.error") | (byte)ControllerError.Overspeed));
                    Fail(AxisError.ControllerFailed);
                    return;
                }

                torque = inputTorque; // en position et vitesse, input_torque sert d'anticipation (passthrough)
                double vErr = 0;
                if (mode >= ControlMode.VelocityControl)
                {
                    vErr = velDes - _velocity;
                    torque += velGain * vErr + _integrator;
                }
                if (mode < ControlMode.VelocityControl && torqueModeVelLimit) // controller.cpp:137-141, 388-394
                {
                    torque = Math.Clamp(torque, (-velLimit - _velocity) * velGain, (velLimit - _velocity) * velGain);
                }
                bool limited = Math.Abs(torque) > torqueLimit;
                torque = Math.Clamp(torque, -torqueLimit, torqueLimit);
                if (mode < ControlMode.VelocityControl)
                {
                    _integrator = 0;
                }
                else
                {
                    _integrator = limited ? _integrator * 0.99 : _integrator + integratorGain * h * vErr;
                }
                Integrate(load, torque, h);
            }

            Set("controller.vel_integrator_torque", (float)_integrator);
            Set("controller.torque_setpoint", (float)torque);
            Set("motor.current_control.Iq_setpoint", (float)(torque / torqueConstant));
            Set("motor.current_control.Iq_measured", (float)(torque / torqueConstant));
        }

        // J·dω/dt = couple − b·ω − τc·signe(ω), avec adhérence à l'arrêt.
        private void Integrate(MechanicalLoad load, double torque, double h)
        {
            if (_velocity == 0 && Math.Abs(torque) <= load.Coulomb)
            {
                return;
            }
            double friction = load.Viscous * _velocity + load.Coulomb * Math.Sign(_velocity == 0 ? torque : _velocity);
            double next = _velocity + (torque - friction) / load.Inertia * h;
            // Le frottement sec arrête la roue au lieu de la faire repartir dans l'autre sens.
            _velocity = _velocity != 0 && Math.Sign(next) != Math.Sign(_velocity) && Math.Abs(torque) <= load.Coulomb ? 0 : next;
            _position += _velocity * h;
        }

        private void Fail(AxisError axis, MotorError motor = MotorError.None, EncoderError encoder = EncoderError.None)
        {
            Set("error", Get<uint>("error") | (uint)axis);
            if (motor != MotorError.None)
            {
                Set("motor.error", Get<ulong>("motor.error") | (ulong)motor);
            }
            if (encoder != EncoderError.None)
            {
                Set("encoder.error", (ushort)(Get<ushort>("encoder.error") | (ushort)encoder));
            }
            _chain.Clear();
            Enter(AxisState.Idle);
        }

        private void Enter(AxisState state)
        {
            _state = state;
            if (state != AxisState.ClosedLoopControl)
            {
                Set("motor.current_control.Iq_setpoint", 0f);
                Set("motor.current_control.Iq_measured", 0f);
            }
            Publish();
        }

        private void Publish()
        {
            Set("current_state", (byte)_state);
            Set("motor.is_armed", _state != AxisState.Idle);
            double noise = motor.Mechanics?.VelocityNoise ?? 0;
            Set("encoder.vel_estimate", (float)(_velocity + (noise > 0 ? noise * Gaussian() : 0)));
            Set("encoder.pos_estimate", (float)_position);
            Set("controller.vel_setpoint", (float)_velocitySetpoint);
        }

        private double Gaussian() =>
            Math.Sqrt(-2 * Math.Log(1 - _noise.NextDouble())) * Math.Cos(2 * Math.PI * _noise.NextDouble());

        private T Get<T>(string path) => (T)Convert.ChangeType(board.Get(prefix + path), typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);

        private void Set(string path, object value) => board.Set(prefix + path, value);
    }
}
