namespace ODrive.Protocol.Simulation;

/// <summary>
/// Valeurs imposées à la carte simulée (v3.6-56V, firmware 0.5.6), par-dessus celles relevées sur une
/// vraie carte (<see cref="SimulatedODrive.CreateReference"/>). Les valeurs de configuration sont les
/// défauts du firmware (fichier cité en regard, relatif à <c>ref/ODrive/Firmware/</c>). Les enums sont
/// donnés par leur valeur numérique : ce projet ne dépend pas du code généré.
/// </summary>
internal static class SimulatedDefaults
{
    public static IEnumerable<KeyValuePair<string, object>> Values
    {
        get
        {
            foreach (KeyValuePair<string, object> entry in Board)
            {
                yield return entry;
            }
            for (int axis = 0; axis < 2; axis++)
            {
                foreach ((string path, object value) in Axis)
                {
                    yield return new($"axis{axis}.{path}", value);
                }
                // MotorControl/axis.hpp:55 puis main.cpp : node_id = numéro de l'axe.
                yield return new($"axis{axis}.config.can.node_id", (uint)axis);
            }
        }
    }

    private static readonly Dictionary<string, object> Board = new()
    {
        ["vbus_voltage"] = 24.0f,
        ["serial_number"] = 0x2065_3884_5748ul,
        ["hw_version_major"] = 3,
        ["hw_version_minor"] = 6,
        ["hw_version_variant"] = 56,
        ["fw_version_major"] = 0,
        ["fw_version_minor"] = 5,
        ["fw_version_revision"] = 6,
        ["fw_version_unreleased"] = 0,
        ["brake_resistor_armed"] = false,
        ["otp_valid"] = true,                               // carte officielle (MotorControl/odrive_main.h:246)
        ["config.enable_brake_resistor"] = false,           // MotorControl/odrive_main.h:82
        ["config.brake_resistance"] = 2.0f,                 // Board/v3/Inc/board.h:41
        ["config.dc_bus_undervoltage_trip_level"] = 8.0f,   // board.h:47
        ["config.dc_bus_overvoltage_trip_level"] = 59.92f,  // odrive_main.h:84 (1,07 × 56 V)
        ["config.dc_max_positive_current"] = float.PositiveInfinity, // odrive_main.h:110
        ["config.dc_max_negative_current"] = -0.01f,        // odrive_main.h:111
        ["config.max_regen_current"] = 0.0f,                // odrive_main.h:80
        ["can.config.baud_rate"] = 250000u,                 // communication/can/odrive_can.hpp:25
    };

    private static readonly Dictionary<string, object> Axis = new()
    {
        ["current_state"] = 1,                              // AXIS_STATE_IDLE
        ["motor.fet_thermistor.temperature"] = 25.0f,       // température ambiante
        ["config.startup_closed_loop_control"] = false,     // MotorControl/axis.hpp:74
        ["config.enable_watchdog"] = false,                 // axis.hpp:87
        ["config.watchdog_timeout"] = 0.0f,                 // axis.hpp:86
        ["motor.config.pole_pairs"] = 7,                    // MotorControl/motor.hpp:19
        ["motor.config.calibration_current"] = 10.0f,       // motor.hpp:20
        ["motor.config.resistance_calib_max_voltage"] = 2.0f, // motor.hpp:21
        ["motor.config.torque_constant"] = 0.04f,           // motor.hpp:24
        ["motor.config.motor_type"] = 0,                    // motor.hpp:25 (HIGH_CURRENT)
        ["motor.config.current_lim"] = 10.0f,               // motor.hpp:28
        ["motor.config.requested_current_range"] = 60.0f,   // motor.hpp:32
        ["motor.config.current_control_bandwidth"] = 1000.0f, // motor.hpp:33
        ["encoder.config.mode"] = 0,                        // MotorControl/encoder.hpp:20 (INCREMENTAL)
        ["encoder.config.calib_scan_distance"] = 50.265484f, // encoder.hpp:22 (16π)
        ["encoder.config.bandwidth"] = 1000.0f,             // encoder.hpp:24
        ["encoder.config.cpr"] = 8192,                      // encoder.hpp:27
        ["controller.config.control_mode"] = 3,             // MotorControl/controller.hpp:25 (POSITION_CONTROL)
        ["controller.config.input_mode"] = 1,               // controller.hpp:26 (PASSTHROUGH)
        ["controller.config.pos_gain"] = 20.0f,             // controller.hpp:27
        ["controller.config.vel_gain"] = 1.0f / 6.0f,       // controller.hpp:28
        ["controller.config.vel_integrator_gain"] = 2.0f / 6.0f, // controller.hpp:30
        ["controller.config.vel_limit"] = 2.0f,             // controller.hpp:31
        ["controller.config.vel_ramp_rate"] = 1.0f,         // controller.hpp:34
        ["controller.config.enable_vel_limit"] = true,      // controller.hpp:45
    };
}
