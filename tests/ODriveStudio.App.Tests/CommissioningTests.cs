using System.IO;
using FluentAssertions;
using ODrive.Model;
using ODrive.Model.Simulation;
using ODrive.Protocol;
using ODrive.Protocol.Simulation;
using ODrive.Tuning.Profiles;
using ODriveStudio.App.ViewModels;
using Xunit;

namespace ODriveStudio.App.Tests;

/// <summary>Carte simulée avec moteur d'hoverboard et horloge avancée par les attentes des vues-modèles.</summary>
internal sealed class MotorBench : IAsyncDisposable
{
    private readonly long[] _ticks = [0];

    private MotorBench(SimulatedODrive board, ODriveDevice device)
    {
        Board = board;
        Device = device;
        Odrv = new ODrive3(device);
        Session = new DeviceSession(device, new SafetyGuard(), Dialogs, Status.Add);
    }

    public SimulatedODrive Board { get; }
    public ODriveDevice Device { get; }
    public ODrive3 Odrv { get; }
    public FakeDialogs Dialogs { get; } = new();
    public List<string> Status { get; } = [];
    public DeviceSession Session { get; }
    public string Folder { get; } = Directory.CreateTempSubdirectory("odrive-studio-").FullName;

    public static async Task<MotorBench> CreateAsync(SimulatedMotorOptions? options = null)
    {
        SimulatedODrive board = SimulatedODrive.CreateReference();
        MotorBench? bench = null;
        long[] ticks = [0];
        SimulatedMotorModel.Attach(board, options, () => new TimeSpan(Interlocked.Read(ref ticks[0])));
        bench = new MotorBench(board, await ODriveDevice.ConnectAsync(new SimulatedTransport(board)));
        bench._ticks[0] = 0;
        bench._shared = ticks;
        return bench;
    }

    private long[] _shared = [0];

    public async Task DelayAsync(TimeSpan duration, CancellationToken ct)
    {
        Interlocked.Add(ref _shared[0], duration.Ticks);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
    }

    public async Task<CommissioningViewModel> CalibratedAsync()
    {
        var wizard = new CommissioningViewModel(Session) { Delay = DelayAsync };
        await wizard.ApplyCommand.ExecuteAsync(null);
        wizard.RebootNeeded = false; // le simulateur n'a pas besoin du redémarrage pour les entrées Hall
        await wizard.RunCalibrationCommand.ExecuteAsync("full");
        return wizard;
    }

    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5);
        }
        condition().Should().BeTrue("la condition attendue n'est pas arrivée en 2 s");
    }

    public async ValueTask DisposeAsync()
    {
        await Device.DisposeAsync();
        Directory.Delete(Folder, recursive: true);
    }
}

public class CommissioningViewModelTests
{
    [Fact]
    public async Task Proposes_the_hoverboard_values_grouped_by_step()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();

        var wizard = new CommissioningViewModel(bench.Session);

        wizard.Settings.Select(row => row.StepName).Distinct().Should().Equal(
            "1. Alimentation", "2. Moteur", "3. Capteur", "4. Régulation", "5. Limites", "6. Communication");
        wizard.Settings.Single(row => row.Path == "axis0.encoder.config.mode").EditText.Should().Be("HALL");
        wizard.Settings.Single(row => row.Path == "axis0.encoder.config.cpr").EditText.Should().Be("90");
        wizard.Settings.Single(row => row.Path == "axis0.motor.config.pole_pairs").EditText.Should().Be("15");
        wizard.Settings.Single(row => row.Path == "config.enable_brake_resistor").EditText.Should().Be("false");
    }

    [Fact]
    public async Task Changing_the_axis_or_the_motor_recomputes_the_proposal()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var wizard = new CommissioningViewModel(bench.Session);

        wizard.Axis = 1;
        wizard.Kv = 8;
        wizard.ProposeCommand.Execute(null);

        wizard.Settings.Should().Contain(row => row.Path == "axis1.motor.config.pole_pairs");
        wizard.Settings.Should().Contain(row => row.Path == "config.gpio12_mode");
        wizard.Settings.Should().NotContain(row => row.Path.StartsWith("axis0.", StringComparison.Ordinal));
        float.Parse(wizard.Settings.Single(row => row.Path == "axis1.motor.config.torque_constant").EditText,
            System.Globalization.CultureInfo.InvariantCulture).Should().BeApproximately(8.27f / 8, 1e-4f);
    }

    // Relevé sur le banc : batterie, pas de résistance de freinage, dc_max_negative_current au défaut
    // (−0,01 A) → ODRIVE_ERROR_DC_BUS_OVER_REGEN_CURRENT trois secondes après l'armement.
    [Fact]
    public async Task Without_a_brake_resistor_the_supply_must_accept_some_regen_current()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var wizard = new CommissioningViewModel(bench.Session);

        wizard.BrakeResistorConnected.Should().BeFalse();
        wizard.SupplyWarning.Should().Contain("DC_BUS_OVER_REGEN_CURRENT");

        wizard.RegenCurrent = 5;
        wizard.ProposeCommand.Execute(null);

        wizard.SupplyWarning.Should().BeEmpty();
        wizard.Settings.Single(row => row.Path == "config.dc_max_negative_current").EditText.Should().Be("-5");

        wizard.RegenCurrent = 0;
        wizard.BrakeResistorConnected = true;
        wizard.ProposeCommand.Execute(null);

        wizard.SupplyWarning.Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_motor_choices_are_reported_and_keep_the_previous_proposal()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var wizard = new CommissioningViewModel(bench.Session);
        int count = wizard.Settings.Count;

        wizard.Kv = 0;
        wizard.ProposeCommand.Execute(null);

        wizard.OptionsMessage.Should().NotBeEmpty();
        wizard.Settings.Should().HaveCount(count);
    }

    [Fact]
    public async Task Refresh_shows_what_differs_from_the_board()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var wizard = new CommissioningViewModel(bench.Session);

        await wizard.RefreshAsync();

        SettingRowViewModel polePairs = wizard.Settings.Single(row => row.Path == "axis0.motor.config.pole_pairs");
        polePairs.CurrentValue.Should().Be("7");
        polePairs.IsDifferent.Should().BeTrue();
        // motor_type vaut déjà HIGH_CURRENT en configuration d'usine.
        wizard.Settings.Single(row => row.Path == "axis0.motor.config.motor_type").IsDifferent.Should().BeFalse();
        wizard.Checklist[0].IsDone.Should().BeFalse();
    }

    [Fact]
    public async Task Apply_writes_the_values_and_asks_for_a_reboot()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var wizard = new CommissioningViewModel(bench.Session);

        await wizard.ApplyCommand.ExecuteAsync(null);

        (await bench.Odrv.Axis0.Motor.Config.PolePairs.GetAsync()).Should().Be(15);
        (await bench.Odrv.Axis0.Encoder.Config.Mode.GetAsync()).Should().Be(EncoderMode.Hall);
        (await bench.Odrv.Axis0.Controller.Config.VelLimit.GetAsync()).Should().Be(10f);
        wizard.Settings.Should().OnlyContain(row => !row.IsDifferent);
        wizard.RebootNeeded.Should().BeTrue("les entrées Hall et la plage de courant ne changent qu'au redémarrage");
        wizard.Checklist[0].IsDone.Should().BeTrue();
        wizard.Checklist[1].IsDone.Should().BeFalse();
        wizard.Log.Should().Contain(line => line.Contains("valeur(s) écrite(s)"));
    }

    [Fact]
    public async Task An_edited_value_is_the_one_written()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var wizard = new CommissioningViewModel(bench.Session);
        wizard.Settings.Single(row => row.Path == "axis0.motor.config.current_lim").EditText = "17.5";

        await wizard.ApplyCommand.ExecuteAsync(null);

        (await bench.Odrv.Axis0.Motor.Config.CurrentLim.GetAsync()).Should().Be(17.5f);
    }

    [Fact]
    public async Task An_invalid_entry_is_flagged_and_the_other_values_are_still_written()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var wizard = new CommissioningViewModel(bench.Session);
        SettingRowViewModel row = wizard.Settings.Single(r => r.Path == "axis0.motor.config.current_lim");
        row.EditText = "beaucoup";

        await wizard.ApplyCommand.ExecuteAsync(null);

        row.Message.Should().NotBeEmpty();
        (await bench.Odrv.Axis0.Motor.Config.CurrentLim.GetAsync()).Should().Be(10f);
        (await bench.Odrv.Axis0.Motor.Config.PolePairs.GetAsync()).Should().Be(15);
        wizard.Log.Should().Contain(line => line.Contains("invalide"));
    }

    [Fact]
    public async Task Nothing_is_written_while_an_axis_is_active()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        CommissioningViewModel wizard = await bench.CalibratedAsync();
        wizard.Settings.Single(row => row.Path == "axis0.motor.config.current_lim").EditText = "17.5";
        await bench.Odrv.Axis0.RequestedState.SetAsync(AxisState.ClosedLoopControl);
        (await bench.Odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.ClosedLoopControl);

        await wizard.ApplyCommand.ExecuteAsync(null);

        (await bench.Odrv.Axis0.Motor.Config.CurrentLim.GetAsync()).Should().Be(10f);
        wizard.Log.Should().Contain(line => line.Contains("IDLE"));
    }

    [Fact]
    public async Task Calibration_is_refused_until_the_pending_reboot_is_done()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var wizard = new CommissioningViewModel(bench.Session) { Delay = bench.DelayAsync };
        await wizard.ApplyCommand.ExecuteAsync(null);

        await wizard.RunCalibrationCommand.ExecuteAsync("full");

        (await bench.Odrv.Axis0.Motor.IsCalibrated.GetAsync()).Should().BeFalse();
        bench.Dialogs.Questions.Should().BeEmpty();
        wizard.Log.Should().Contain(line => line.Contains("sauvegarder et redémarrer"));
    }

    [Fact]
    public async Task Full_calibration_reports_plausible_measurements_and_ticks_the_checklist()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();

        CommissioningViewModel wizard = await bench.CalibratedAsync();

        bench.Dialogs.Questions.Should().ContainSingle().Which.Should().Contain("roue");
        wizard.IsCalibrating.Should().BeFalse();
        wizard.Log.Should().Contain(line => line.Contains("0.180 Ω") && line.Contains("0.340 mH"));
        wizard.Log.Should().Contain(line => line.Contains("Mesures plausibles"));
        wizard.Log.Should().Contain(line => line.Contains("Offset plausible"));
        wizard.Checklist[2].IsDone.Should().BeTrue();
        wizard.Checklist[3].IsDone.Should().BeTrue();
        wizard.Checklist[4].IsDone.Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_calibration_shows_the_decoded_error()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var wizard = new CommissioningViewModel(bench.Session) { Delay = bench.DelayAsync };

        // Configuration d'usine (7 paires de pôles, capteur incrémental) sur un moteur d'hoverboard.
        await wizard.RunCalibrationCommand.ExecuteAsync("full");

        wizard.Log.Should().Contain(line => line.Contains("CPR_POLEPAIRS_MISMATCH"));
        wizard.Checklist[3].IsDone.Should().BeFalse();
    }

    [Fact]
    public async Task An_implausible_motor_is_flagged()
    {
        await using MotorBench bench = await MotorBench.CreateAsync(new SimulatedMotorOptions(PhaseResistance: 0.02f));

        CommissioningViewModel wizard = await bench.CalibratedAsync();

        wizard.Log.Should().Contain(line => line.Contains("Résistance de phase inhabituelle"));
    }

    [Fact]
    public async Task Nothing_runs_when_the_user_declines_the_motion()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var wizard = new CommissioningViewModel(bench.Session) { Delay = bench.DelayAsync };
        bench.Dialogs.Answer = false;

        await wizard.RunCalibrationCommand.ExecuteAsync("motor");

        (await bench.Odrv.Axis0.Motor.IsCalibrated.GetAsync()).Should().BeFalse();
        wizard.Log.Should().Contain(line => line.Contains("annulée"));
    }

    [Fact]
    public async Task Pre_calibrated_is_refused_before_a_successful_calibration()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var wizard = new CommissioningViewModel(bench.Session);

        await wizard.MarkPreCalibratedCommand.ExecuteAsync(null);

        (await bench.Odrv.Axis0.Motor.Config.PreCalibrated.GetAsync()).Should().BeFalse();
        wizard.Log.Should().Contain(line => line.Contains("refusée"));
    }

    [Fact]
    public async Task Pre_calibrated_then_save_makes_the_calibration_survive_a_reboot()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        CommissioningViewModel wizard = await bench.CalibratedAsync();
        wizard.StartupClosedLoop = true;

        await wizard.MarkPreCalibratedCommand.ExecuteAsync(null);
        wizard.Checklist[4].IsDone.Should().BeTrue();
        await wizard.SaveAndRebootCommand.ExecuteAsync(null);

        ODriveDevice again = await ODriveDevice.ConnectAsync(new SimulatedTransport(bench.Board));
        var odrv = new ODrive3(again);
        (await odrv.Axis0.Motor.IsCalibrated.GetAsync()).Should().BeTrue();
        (await odrv.Axis0.Encoder.IsReady.GetAsync()).Should().BeTrue();
        (await odrv.Axis0.Config.StartupClosedLoopControl.GetAsync()).Should().BeTrue();
        (await odrv.Axis0.Motor.Config.PolePairs.GetAsync()).Should().Be(15);
        wizard.Log.Should().Contain(line => line.Contains("sauvegardée"));
        await again.DisposeAsync();
    }

    [Fact]
    public async Task Copy_to_the_other_axis_asks_first_then_lists_what_changed()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var wizard = new CommissioningViewModel(bench.Session);
        await wizard.ApplyCommand.ExecuteAsync(null);
        wizard.RebootNeeded = false;

        bench.Dialogs.Answer = false;
        await wizard.CopyToOtherAxisCommand.ExecuteAsync(null);
        (await bench.Odrv.Axis1.Motor.Config.PolePairs.GetAsync()).Should().Be(7);

        bench.Dialogs.Answer = true;
        await wizard.CopyToOtherAxisCommand.ExecuteAsync(null);

        bench.Dialogs.Questions.Last().Should().Contain("axe 0").And.Contain("axe 1");
        (await bench.Odrv.Axis1.Motor.Config.PolePairs.GetAsync()).Should().Be(15);
        (await bench.Odrv.Axis1.Config.Can.NodeId.GetAsync()).Should().Be(1u);
        wizard.Log.Should().Contain(line => line.Contains("axis1.motor.config.pole_pairs") && line.Contains("15"));
        wizard.Log.Last().Should().Contain("calibr");
        // Les entrées Hall de l'autre axe ne prennent effet qu'après sauvegarde et redémarrage.
        wizard.RebootNeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Copy_to_the_other_axis_is_refused_while_an_axis_is_active()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        await bench.CalibratedAsync();
        await bench.Odrv.Axis0.RequestedState.SetAsync(AxisState.ClosedLoopControl);
        var wizard = new CommissioningViewModel(bench.Session);

        await wizard.CopyToOtherAxisCommand.ExecuteAsync(null);

        wizard.Log.Last().Should().Contain("refusée");
        (await bench.Odrv.Axis1.Motor.Config.PolePairs.GetAsync()).Should().Be(7);
    }

    [Fact]
    public async Task Save_asks_for_confirmation()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var wizard = new CommissioningViewModel(bench.Session);
        await wizard.ApplyCommand.ExecuteAsync(null);
        bench.Dialogs.Answer = false;

        await wizard.SaveAndRebootCommand.ExecuteAsync(null);

        bench.Dialogs.Questions.Should().ContainSingle();
        bench.Device.IsConnected.Should().BeTrue("la carte n'a pas redémarré");
        wizard.RebootNeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Export_writes_a_profile_of_the_board()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var wizard = new CommissioningViewModel(bench.Session);
        await wizard.ApplyCommand.ExecuteAsync(null);
        bench.Dialogs.SavePath = Path.Combine(bench.Folder, "profil.json");

        await wizard.ExportProfileCommand.ExecuteAsync(null);

        await using FileStream stream = File.OpenRead(bench.Dialogs.SavePath);
        ConfigProfile profile = ConfigProfile.Load(stream);
        profile.Values["axis0.motor.config.pole_pairs"].Should().Be("15");
        profile.Note.Should().Contain("hoverboard");
    }
}

public class JogViewModelTests
{
    [Fact]
    public async Task Is_refused_with_an_explanation_on_an_uncalibrated_axis()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var jog = new JogViewModel(bench.Session) { Delay = bench.DelayAsync };

        await jog.StartCommand.ExecuteAsync(null);

        jog.IsRunning.Should().BeFalse();
        jog.Message.Should().Contain("calibrés");
        jog.StopCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Hold_buttons_run_the_motor_and_releasing_stops_it()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        await bench.CalibratedAsync();
        var jog = new JogViewModel(bench.Session) { Delay = bench.DelayAsync, RampRate = 50f, JogSpeed = 1.5f };
        await jog.StartCommand.ExecuteAsync(null);
        jog.IsRunning.Should().BeTrue();
        jog.IsStopped.Should().BeFalse();

        await jog.PressAsync(-1);
        (await bench.Odrv.Axis0.Controller.InputVel.GetAsync()).Should().Be(-1.5f);
        await MotorBench.WaitUntilAsync(() => jog.Velocity.StartsWith("-1.5", StringComparison.Ordinal));

        await jog.ReleaseAsync();
        (await bench.Odrv.Axis0.Controller.InputVel.GetAsync()).Should().Be(0f);
        jog.Command.Should().Be(0f);
        await MotorBench.WaitUntilAsync(() => jog.Velocity is "0.00 tours/s" or "-0.00 tours/s");
        await jog.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task In_torque_mode_the_hold_buttons_command_a_torque()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        await bench.CalibratedAsync();
        var jog = new JogViewModel(bench.Session) { Delay = bench.DelayAsync, MaxTorque = 1f, JogTorque = 0.4f };
        jog.Mode = jog.Modes[1];
        jog.IsTorqueMode.Should().BeTrue();
        jog.CommandUnit.Should().Be("Nm");
        jog.CommandLimit.Should().Be(1f);

        await jog.StartCommand.ExecuteAsync(null);
        jog.Message.Should().Contain("couple");

        await jog.PressAsync(-1);
        (await bench.Odrv.Axis0.Controller.InputTorque.GetAsync()).Should().Be(-0.4f);
        jog.Command.Should().Be(-0.4f);

        await jog.SetCommandAsync(5f);
        (await bench.Odrv.Axis0.Controller.InputTorque.GetAsync()).Should().Be(1f);

        await jog.ReleaseAsync();
        (await bench.Odrv.Axis0.Controller.InputTorque.GetAsync()).Should().Be(0f);
        await jog.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Commands_are_clamped_to_the_maximum_velocity()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        await bench.CalibratedAsync();
        var jog = new JogViewModel(bench.Session) { Delay = bench.DelayAsync, MaxVelocity = 1f, JogSpeed = 5f };
        await jog.StartCommand.ExecuteAsync(null);

        await jog.PressAsync(+1);

        jog.Command.Should().Be(1f);
        (await bench.Odrv.Axis0.Controller.InputVel.GetAsync()).Should().Be(1f);
        await jog.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Stopping_puts_the_axis_in_idle_and_restores_the_board_settings()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        await bench.CalibratedAsync();
        ConfigProfile before = await ConfigProfile.CaptureAsync(bench.Device);
        var jog = new JogViewModel(bench.Session) { Delay = bench.DelayAsync, MaxVelocity = 1f, MaxCurrent = 4f };
        await jog.StartCommand.ExecuteAsync(null);
        await jog.SetCommandAsync(1f);

        await jog.StopCommand.ExecuteAsync(null);

        jog.IsRunning.Should().BeFalse();
        jog.Message.Should().Be("Essai terminé.");
        (await bench.Odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.Idle);
        (await before.CompareToDeviceAsync(bench.Device)).Should().BeEmpty();
        jog.StartCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task The_emergency_stop_ends_the_session()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        await bench.CalibratedAsync();
        var jog = new JogViewModel(bench.Session) { Delay = bench.DelayAsync };
        await jog.StartCommand.ExecuteAsync(null);
        await jog.SetCommandAsync(1f);

        await EmergencyStop.StopAsync([bench.Device]);

        await MotorBench.WaitUntilAsync(() => !jog.IsRunning);
        jog.Message.Should().Contain("abandonné");
        jog.Command.Should().Be(0f);
        (await bench.Odrv.Axis0.Config.EnableWatchdog.GetAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Commands_are_ignored_when_no_session_is_running()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var jog = new JogViewModel(bench.Session);

        await jog.PressAsync(+1);

        jog.Command.Should().Be(0f);
        (await bench.Odrv.Axis0.Controller.InputVel.GetAsync()).Should().Be(0f);
    }
}

public class ProfilesViewModelTests
{
    private static async Task<string> ExportAsync(MotorBench bench, ProfilesViewModel profiles, string name)
    {
        bench.Dialogs.SavePath = Path.Combine(bench.Folder, name);
        await profiles.ExportCommand.ExecuteAsync(null);
        return bench.Dialogs.SavePath;
    }

    [Fact]
    public async Task Export_then_import_shows_no_difference()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var profiles = new ProfilesViewModel(bench.Session);
        bench.Dialogs.OpenPath = await ExportAsync(bench, profiles, "usine.json");

        await profiles.ImportCommand.ExecuteAsync(null);

        profiles.HasProfile.Should().BeTrue();
        profiles.ProfileSummary.Should().Contain("usine.json").And.Contain("0.5.6");
        profiles.Differences.Should().BeEmpty();
        profiles.Warning.Should().BeEmpty();
        profiles.Message.Should().Contain("exactement");
    }

    [Fact]
    public async Task Import_lists_the_differences_without_changing_the_board()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var profiles = new ProfilesViewModel(bench.Session);
        bench.Dialogs.OpenPath = await ExportAsync(bench, profiles, "usine.json");
        await bench.Odrv.Axis0.Controller.Config.VelLimit.SetAsync(7.5f);
        await bench.Odrv.Axis1.Motor.Config.PolePairs.SetAsync(15);

        await profiles.ImportCommand.ExecuteAsync(null);

        profiles.Differences.Select(row => (row.Path, row.ProfileValue, row.DeviceValue)).Should().Equal(
            ("axis0.controller.config.vel_limit", "2", "7.5"),
            ("axis1.motor.config.pole_pairs", "7", "15"));
        profiles.Differences.Should().OnlyContain(row => !row.IsSelected);
        (await bench.Odrv.Axis0.Controller.Config.VelLimit.GetAsync()).Should().Be(7.5f);
    }

    [Fact]
    public async Task Only_the_ticked_differences_are_written_after_confirmation()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var profiles = new ProfilesViewModel(bench.Session);
        bench.Dialogs.OpenPath = await ExportAsync(bench, profiles, "usine.json");
        await bench.Odrv.Axis0.Controller.Config.VelLimit.SetAsync(7.5f);
        await bench.Odrv.Axis1.Motor.Config.PolePairs.SetAsync(15);
        await profiles.ImportCommand.ExecuteAsync(null);
        profiles.Differences.Single(row => row.Path == "axis0.controller.config.vel_limit").IsSelected = true;

        await profiles.ApplySelectedCommand.ExecuteAsync(null);

        bench.Dialogs.Questions.Should().ContainSingle().Which.Should().Contain("1 valeur");
        (await bench.Odrv.Axis0.Controller.Config.VelLimit.GetAsync()).Should().Be(2f);
        (await bench.Odrv.Axis1.Motor.Config.PolePairs.GetAsync()).Should().Be(15);
        profiles.Differences.Should().ContainSingle().Which.Path.Should().Be("axis1.motor.config.pole_pairs");
    }

    [Fact]
    public async Task Select_all_then_apply_restores_the_whole_profile()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var profiles = new ProfilesViewModel(bench.Session);
        bench.Dialogs.OpenPath = await ExportAsync(bench, profiles, "usine.json");
        var wizard = new CommissioningViewModel(bench.Session);
        await wizard.ApplyCommand.ExecuteAsync(null);
        await profiles.ImportCommand.ExecuteAsync(null);
        profiles.Differences.Should().NotBeEmpty();

        profiles.SelectAllCommand.Execute(null);
        await profiles.ApplySelectedCommand.ExecuteAsync(null);

        profiles.Differences.Should().BeEmpty();
        (await bench.Odrv.Axis0.Motor.Config.PolePairs.GetAsync()).Should().Be(7);
    }

    [Fact]
    public async Task Nothing_is_written_when_the_user_declines_or_nothing_is_ticked()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var profiles = new ProfilesViewModel(bench.Session);
        bench.Dialogs.OpenPath = await ExportAsync(bench, profiles, "usine.json");
        await bench.Odrv.Axis0.Controller.Config.VelLimit.SetAsync(7.5f);
        await profiles.ImportCommand.ExecuteAsync(null);

        await profiles.ApplySelectedCommand.ExecuteAsync(null);
        profiles.Message.Should().Contain("Aucun écart coché");
        bench.Dialogs.Questions.Should().BeEmpty();

        profiles.SelectAllCommand.Execute(null);
        bench.Dialogs.Answer = false;
        await profiles.ApplySelectedCommand.ExecuteAsync(null);

        (await bench.Odrv.Axis0.Controller.Config.VelLimit.GetAsync()).Should().Be(7.5f);
    }

    [Fact]
    public async Task Nothing_is_written_while_an_axis_is_active()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var profiles = new ProfilesViewModel(bench.Session);
        bench.Dialogs.OpenPath = await ExportAsync(bench, profiles, "usine.json");
        await bench.CalibratedAsync();
        await profiles.ImportCommand.ExecuteAsync(null);
        profiles.SelectAllCommand.Execute(null);
        await bench.Odrv.Axis0.RequestedState.SetAsync(AxisState.ClosedLoopControl);
        (await bench.Odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.ClosedLoopControl);

        await profiles.ApplySelectedCommand.ExecuteAsync(null);

        profiles.Message.Should().Contain("IDLE");
        (await bench.Odrv.Axis0.Motor.Config.PolePairs.GetAsync()).Should().Be(15);
    }

    [Fact]
    public async Task A_file_that_is_not_a_profile_is_refused()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var profiles = new ProfilesViewModel(bench.Session);
        bench.Dialogs.OpenPath = Path.Combine(bench.Folder, "autre.json");
        await File.WriteAllTextAsync(bench.Dialogs.OpenPath, """{"values":{}}""");

        await profiles.ImportCommand.ExecuteAsync(null);

        profiles.HasProfile.Should().BeFalse();
        profiles.Message.Should().Contain("autre.json").And.Contain("profil ODrive Studio");
        profiles.ApplySelectedCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task A_profile_from_another_firmware_shows_a_warning()
    {
        await using MotorBench bench = await MotorBench.CreateAsync();
        var profiles = new ProfilesViewModel(bench.Session);
        ConfigProfile captured = await ConfigProfile.CaptureAsync(bench.Device);
        var other = new ConfigProfile("0.5.1", "v3.6-56V", 1, "ancien", captured.Created, captured.Values);
        bench.Dialogs.OpenPath = Path.Combine(bench.Folder, "ancien.json");
        await using (FileStream stream = File.Create(bench.Dialogs.OpenPath))
        {
            other.Save(stream);
        }

        await profiles.ImportCommand.ExecuteAsync(null);

        profiles.Warning.Should().Contain("0.5.1");
        profiles.ProfileSummary.Should().Contain("ancien");
    }
}
