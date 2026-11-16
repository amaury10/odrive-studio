using FluentAssertions;
using ODrive.Model;
using ODrive.Protocol;
using ODriveStudio.App.ViewModels;
using Xunit;

namespace ODriveStudio.App.Tests;

public class ValueTextTests
{
    private static readonly EnumMetadata AxisStates = ODriveEnums.Get<AxisState>();
    private static readonly EnumMetadata AxisErrors = ODriveEnums.Get<AxisError>();

    [Theory]
    [InlineData(1.5f, "1.5")]
    [InlineData(0.16666667f, "0.166667")]
    [InlineData(float.PositiveInfinity, "inf")]
    [InlineData(float.NaN, "nan")]
    [InlineData(true, "true")]
    [InlineData(42u, "42")]
    public void Format_prints_plain_values_with_invariant_culture(object value, string expected)
    {
        ValueText.Format(value, null).Should().Be(expected);
    }

    [Fact]
    public void Format_names_enum_values_and_error_flags()
    {
        ValueText.Format((byte)8, AxisStates).Should().Be("CLOSED_LOOP_CONTROL (8)");
        ValueText.Format((byte)200, AxisStates).Should().Be("200");
        ValueText.Format(0x140u, AxisErrors).Should().Be("0x140 (MOTOR_FAILED | ENCODER_FAILED)");
        ValueText.Format(0u, AxisErrors).Should().Be("0");
    }

    [Theory]
    [InlineData("1.5", EndpointValueType.Float, 1.5f)]
    [InlineData("1,5", EndpointValueType.Float, 1.5f)]   // virgule décimale française
    [InlineData(" -2 ", EndpointValueType.Float, -2f)]
    [InlineData("inf", EndpointValueType.Float, float.PositiveInfinity)]
    [InlineData("15", EndpointValueType.Int32, 15)]
    [InlineData("0x10", EndpointValueType.UInt32, 16u)]
    [InlineData("true", EndpointValueType.Bool, true)]
    [InlineData("0", EndpointValueType.Bool, false)]
    [InlineData("oui", EndpointValueType.Bool, true)]
    public void Parse_reads_user_input(string text, EndpointValueType type, object expected)
    {
        ValueText.Parse(text, type, null).Should().Be(expected);
    }

    [Theory]
    [InlineData("CLOSED_LOOP_CONTROL")]
    [InlineData("closed_loop_control")]
    [InlineData("AXIS_STATE_CLOSED_LOOP_CONTROL")]
    [InlineData("CLOSED_LOOP_CONTROL (8)")]
    [InlineData("8")]
    public void Parse_accepts_enum_names_and_numbers(string text)
    {
        ValueText.Parse(text, EndpointValueType.UInt8, AxisStates).Should().Be((byte)8);
    }

    [Theory]
    [InlineData("abc", EndpointValueType.Float)]
    [InlineData("", EndpointValueType.Int32)]
    [InlineData("300", EndpointValueType.UInt8)]
    [InlineData("-1", EndpointValueType.UInt32)]
    [InlineData("peut-être", EndpointValueType.Bool)]
    public void Parse_rejects_invalid_input_with_a_readable_message(string text, EndpointValueType type)
    {
        Action act = () => ValueText.Parse(text, type, null);

        act.Should().Throw<FormatException>().Which.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Parse_rejects_an_unknown_enum_name()
    {
        Action act = () => ValueText.Parse("FLYING", EndpointValueType.UInt8, AxisStates);

        act.Should().Throw<FormatException>().WithMessage("*FLYING*");
    }
}
