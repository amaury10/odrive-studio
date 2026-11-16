using System.Text;
using FluentAssertions;
using Xunit;

namespace ODrive.Protocol.Tests;

public class EndpointSchemaTests
{
    private static readonly EndpointSchema Reference = EndpointSchema.Parse(TestData.ReferenceJson());

    [Fact]
    public void Parse_computes_json_crc_and_version_id()
    {
        Reference.JsonCrc.Should().Be(TestData.ReferenceJsonCrc);
        Reference.JsonVersionId.Should().Be(TestData.ReferenceJsonVersionId);
    }

    [Fact]
    public void Parse_finds_all_properties_and_functions_of_the_reference_json()
    {
        // 734 propriétés (l'élément « json » d'id 0 est ignoré) et 28 fonctions.
        Reference.Properties.Should().HaveCount(734);
        Reference.Functions.Should().HaveCount(28);
    }

    [Theory]
    [InlineData("vbus_voltage", 2, EndpointValueType.Float, false)]
    [InlineData("serial_number", 5, EndpointValueType.UInt64, false)]
    [InlineData("axis0.requested_state", 135, EndpointValueType.UInt8, true)]
    [InlineData("axis0.motor.error", 185, EndpointValueType.UInt64, true)]
    [InlineData("axis0.controller.config.vel_limit", 281, EndpointValueType.Float, true)]
    [InlineData("axis1.controller.input_vel", 579, EndpointValueType.Float, true)]
    public void FindProperty_returns_id_type_and_access(string path, int id, EndpointValueType type, bool writable)
    {
        PropertyNode? property = Reference.FindProperty(path);

        property.Should().NotBeNull();
        property!.Path.Should().Be(path);
        property.Name.Should().Be(path[(path.LastIndexOf('.') + 1)..]);
        property.Id.Should().Be((ushort)id);
        property.Type.Should().Be(type);
        property.IsWritable.Should().Be(writable);
    }

    [Fact]
    public void FindFunction_returns_arguments_with_their_own_ids()
    {
        FunctionNode? function = Reference.FindFunction("get_adc_voltage");

        function.Should().NotBeNull();
        function!.Id.Should().Be(769);
        function.Inputs.Should().ContainSingle();
        function.Inputs[0].Should().BeEquivalentTo(new { Name = "gpio", Id = (ushort)770, Type = EndpointValueType.UInt32 });
        function.Outputs.Should().ContainSingle();
        function.Outputs[0].Should().BeEquivalentTo(new { Name = "voltage", Id = (ushort)771, Type = EndpointValueType.Float });
    }

    [Fact]
    public void Function_arguments_are_not_listed_as_properties()
    {
        Reference.FindProperty("get_adc_voltage.gpio").Should().BeNull();
        Reference.Properties.Should().NotContain(p => p.Id == 770);
    }

    [Fact]
    public void Root_exposes_the_tree_in_json_order()
    {
        Reference.Root.Members[0].Name.Should().Be("error");
        Reference.Root.Members[1].Name.Should().Be("vbus_voltage");
        ObjectNode axis0 = Reference.Root.Members.OfType<ObjectNode>().Single(o => o.Name == "axis0");
        axis0.Path.Should().Be("axis0");
        axis0.Members.OfType<ObjectNode>().Select(o => o.Path).Should().Contain("axis0.motor");
    }

    [Fact]
    public void Find_returns_null_for_unknown_paths()
    {
        Reference.FindProperty("axis0.nope").Should().BeNull();
        Reference.FindFunction("vbus_voltage").Should().BeNull();
    }

    [Fact]
    public void Parse_rejects_unknown_value_types()
    {
        byte[] json = Encoding.ASCII.GetBytes("""[{"name":"x","id":1,"type":"complex128","access":"r"}]""");

        Action act = () => EndpointSchema.Parse(json);

        act.Should().Throw<FormatException>().WithMessage("*complex128*");
    }
}
