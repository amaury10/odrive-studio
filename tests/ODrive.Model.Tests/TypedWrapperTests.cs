using System.Reflection;
using System.Runtime.InteropServices;
using FluentAssertions;
using ODrive.Protocol;
using ODrive.Protocol.Simulation;
using Xunit;

namespace ODrive.Model.Tests;

public class TypedWrapperTests
{
    private readonly SimulatedODrive _board = SimulatedODrive.CreateReference();

    private async Task<ODrive3> ConnectAsync()
    {
        ODriveDevice device = await ODriveDevice.ConnectAsync(new SimulatedTransport(_board));
        return new ODrive3(device);
    }

    [Fact]
    public async Task Root_property_is_read_with_its_type()
    {
        ODrive3 odrv = await ConnectAsync();
        _board.Set("vbus_voltage", 23.5f);

        (await odrv.VbusVoltage.GetAsync()).Should().Be(23.5f);
    }

    [Fact]
    public async Task Nested_property_is_written_at_its_firmware_path()
    {
        ODrive3 odrv = await ConnectAsync();

        await odrv.Axis1.Controller.Config.VelLimit.SetAsync(6.5f);

        odrv.Axis1.Controller.Config.VelLimit.Path.Should().Be("axis1.controller.config.vel_limit");
        _board.Get("axis1.controller.config.vel_limit").Should().Be(6.5f);
    }

    [Fact]
    public async Task Enum_properties_use_the_generated_enums()
    {
        ODrive3 odrv = await ConnectAsync();
        _board.Set("axis0.error", 0x40u | 0x100u);

        (await odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.Idle);
        (await odrv.Axis0.Error.GetAsync()).Should().Be(AxisError.MotorFailed | AxisError.EncoderFailed);

        await odrv.Axis0.RequestedState.SetAsync(AxisState.ClosedLoopControl);
        _board.Get("axis0.requested_state").Should().Be((byte)8);
    }

    [Fact]
    public async Task Function_with_input_and_output_is_a_typed_method()
    {
        ODrive3 odrv = await ConnectAsync();
        _board.OnCall("get_adc_voltage", call => call.SetOutput("voltage", (uint)call.Input("gpio") * 0.25f));

        float voltage = await odrv.GetAdcVoltageAsync(4);

        voltage.Should().Be(1.0f);
    }

    [Fact]
    public async Task Function_without_arguments_is_a_typed_method()
    {
        ODrive3 odrv = await ConnectAsync();
        _board.Set("axis0.motor.error", 0x10ul);

        await odrv.ClearErrorsAsync();

        _board.Get("axis0.motor.error").Should().Be(0ul);
    }

    [Fact]
    public async Task Every_wrapper_property_exists_on_the_reference_board_with_a_matching_type()
    {
        ODrive3 odrv = await ConnectAsync();
        var seen = new List<string>();

        Visit(odrv);

        seen.Should().BeEquivalentTo(ODriveInterface.Properties.Select(p => p.Path));

        void Visit(RemoteObject node)
        {
            foreach (PropertyInfo member in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                object? value = member.GetValue(node);
                if (value is RemoteObject child)
                {
                    Visit(child);
                }
                else if (member.PropertyType.IsGenericType
                         && member.PropertyType.GetGenericTypeDefinition() == typeof(RemoteProperty<>))
                {
                    var schemaNode = (PropertyNode)member.PropertyType.GetProperty("Node")!.GetValue(value)!;
                    Type valueType = member.PropertyType.GetGenericArguments()[0];
                    Type wireType = valueType.IsEnum ? Enum.GetUnderlyingType(valueType) : valueType;
                    int size = wireType == typeof(bool) ? 1 : Marshal.SizeOf(wireType);
                    size.Should().Be(EndpointValue.SizeOf(schemaNode.Type), "type de {0}", schemaNode.Path);
                    seen.Add(schemaNode.Path);
                }
            }
        }
    }
}
