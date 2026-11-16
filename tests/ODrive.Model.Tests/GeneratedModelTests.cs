using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace ODrive.Model.Tests;

/// <summary>
/// Vérifie le code généré contre le JSON d'endpoints du firmware 0.5.6 (produit par le générateur Python
/// de référence, tools/gen_reference_json.py) et contre quelques valeurs relevées dans les sources.
/// </summary>
public class GeneratedModelTests
{
    private sealed record JsonEndpoint(string Path, string Type, string? Access, string[] Inputs, string[] Outputs);

    private static List<JsonEndpoint> LoadReferenceJson()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "data", "odrive-v3.6-0.5.6.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new List<JsonEndpoint>();
        Walk(doc.RootElement, "");
        return result;

        void Walk(JsonElement members, string prefix)
        {
            foreach (var member in members.EnumerateArray())
            {
                string name = member.GetProperty("name").GetString()!;
                string type = member.GetProperty("type").GetString()!;
                switch (type)
                {
                    case "json":
                        break;
                    case "object":
                        Walk(member.GetProperty("members"), prefix + name + ".");
                        break;
                    case "function":
                        result.Add(new JsonEndpoint(prefix + name, type, null, Args(member, "inputs"), Args(member, "outputs")));
                        break;
                    default:
                        result.Add(new JsonEndpoint(prefix + name, type, member.GetProperty("access").GetString(), [], []));
                        break;
                }
            }
        }

        static string[] Args(JsonElement function, string key) =>
            [.. function.GetProperty(key).EnumerateArray()
                .Select(a => $"{a.GetProperty("name").GetString()}:{a.GetProperty("type").GetString()}")];
    }

    [Fact]
    public void Properties_match_the_firmware_json_in_order()
    {
        var expected = LoadReferenceJson().Where(e => e.Type != "function")
            .Select(e => (e.Path, e.Type, Writable: e.Access == "rw"));

        ODriveInterface.Properties.Select(p => (p.Path, Type: p.JsonType, Writable: p.IsWritable))
            .Should().Equal(expected);
    }

    [Fact]
    public void Functions_match_the_firmware_json_in_order()
    {
        var expected = LoadReferenceJson().Where(e => e.Type == "function")
            .Select(e => $"{e.Path}({string.Join(",", e.Inputs)})->({string.Join(",", e.Outputs)})");

        ODriveInterface.Functions
            .Select(f => $"{f.Path}({string.Join(",", f.Inputs.Select(a => $"{a.Name}:{a.JsonType}"))})" +
                         $"->({string.Join(",", f.Outputs.Select(a => $"{a.Name}:{a.JsonType}"))})")
            .Should().Equal(expected);
    }

    [Fact]
    public void Enum_values_match_the_firmware_sources()
    {
        // ref/ODrive/Firmware/odrive-interface.yaml, ODrive.Axis.AxisState (l.1523) : valeur explicite 6
        // pour ENCODER_INDEX_SEARCH, donc pas de valeur 5.
        ((int)AxisState.Undefined).Should().Be(0);
        ((int)AxisState.Idle).Should().Be(1);
        ((int)AxisState.MotorCalibration).Should().Be(4);
        ((int)AxisState.EncoderIndexSearch).Should().Be(6);
        ((int)AxisState.EncoderOffsetCalibration).Should().Be(7);
        ((int)AxisState.ClosedLoopControl).Should().Be(8);
        ((int)AxisState.EncoderHallPolarityCalibration).Should().Be(12);
        Enum.IsDefined((AxisState)5).Should().BeFalse();

        // ODrive.Encoder.Mode (l.1582)
        ((int)EncoderMode.Incremental).Should().Be(0);
        ((int)EncoderMode.Hall).Should().Be(1);
        ((int)EncoderMode.SpiAbsCui).Should().Be(0x100);

        // ODrive.Motor.MotorType (l.1727) : GIMBAL = 2 explicite, ACIM suit.
        ((int)MotorType.HighCurrent).Should().Be(0);
        ((int)MotorType.Gimbal).Should().Be(2);
        ((int)MotorType.Acim).Should().Be(3);

        ((int)ControlMode.VelocityControl).Should().Be(2);
        ((int)InputMode.VelRamp).Should().Be(2);
    }

    [Fact]
    public void Error_flags_honour_explicit_bit_positions()
    {
        // ODrive.Axis.Error (l.439) : bits explicites 6, 11 et 17.
        ((uint)AxisError.InvalidState).Should().Be(1u << 0);
        ((uint)AxisError.MotorFailed).Should().Be(1u << 6);
        ((uint)AxisError.EncoderFailed).Should().Be(1u << 8);
        ((uint)AxisError.WatchdogTimerExpired).Should().Be(1u << 11);
        ((uint)AxisError.EstopRequested).Should().Be(1u << 14);
        ((uint)AxisError.HomingWithoutEndstop).Should().Be(1u << 17);
        ((uint)AxisError.UnknownPosition).Should().Be(1u << 19);

        // Même codage que odrivetool : ENCODER_ERROR_ILLEGAL_HALL_STATE = 0x10.
        ((uint)EncoderError.IllegalHallState).Should().Be(0x10);
    }

    [Fact]
    public void Every_enum_has_metadata_consistent_with_its_csharp_type()
    {
        ODriveEnums.All.Should().HaveCount(15);
        foreach (var meta in ODriveEnums.All)
        {
            meta.Type.IsEnum.Should().BeTrue();
            var csValues = Enum.GetValues(meta.Type).Cast<object>().Select(Convert.ToUInt64).Order();
            meta.Values.Select(v => v.Value).Order().Should().Equal(csValues, meta.FirmwareName);
        }

        ODriveEnums.Get<AxisState>().Prefix.Should().Be("AXIS_STATE");
        ODriveEnums.Get<EncoderMode>().Prefix.Should().Be("ENCODER_MODE");
        ODriveEnums.Get<MotorError>().IsFlags.Should().BeTrue();
    }

    [Fact]
    public void Flags_decode_to_firmware_names()
    {
        var meta = ODriveEnums.Get<AxisError>();
        var raw = (ulong)(AxisError.MotorFailed | AxisError.WatchdogTimerExpired);

        meta.Decode(raw).Select(meta.DisplayName)
            .Should().Equal("AXIS_ERROR_MOTOR_FAILED", "AXIS_ERROR_WATCHDOG_TIMER_EXPIRED");
        meta.Decode(0).Should().BeEmpty();

        ODriveEnums.Get<AxisState>().Decode(8).Select(v => v.Name).Should().Equal("CLOSED_LOOP_CONTROL");
    }

    [Fact]
    public void Property_metadata_carries_units_enums_and_access()
    {
        var velGain = ODriveInterface.FindProperty("axis0.controller.config.vel_gain")!;
        velGain.JsonType.Should().Be("float");
        velGain.IsWritable.Should().BeTrue();
        velGain.Unit.Should().Be("N·m / (turn/s)");

        ODriveInterface.FindProperty("vbus_voltage")!.IsWritable.Should().BeFalse();
        ODriveInterface.FindProperty("axis1.requested_state")!.EnumType.Should().Be<AxisState>();
        ODriveInterface.FindProperty("axis0.encoder.config.mode")!.EnumType.Should().Be<EncoderMode>();
        ODriveInterface.FindProperty("config.gpio9_mode")!.EnumType.Should().Be<GpioMode>();
        ODriveInterface.FindProperty("axis0.config.can.heartbeat_rate_ms").Should().NotBeNull();
        ODriveInterface.FindProperty("does.not.exist").Should().BeNull();

        var getValue = ODriveInterface.FindFunction("axis0.controller.get_anticogging_value")!;
        getValue.Inputs.Should().ContainSingle().Which.Name.Should().Be("index");
        getValue.Outputs.Should().ContainSingle().Which.JsonType.Should().Be("float");
    }
}
