using System.Text.Json;
using System.Text.Json.Protobuf.Tests;
using Google.Protobuf;
using Protobuf.System.Text.Json.Tests.Utils;
using Xunit;
using Xunit.Abstractions;

namespace Protobuf.System.Text.Json.Tests;

public class MessageWithExtensionsTests
{
    private readonly ITestOutputHelper _output;

    public MessageWithExtensionsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Should_serialize_message_with_string_extension()
    {
        // Arrange
        var msg = new BaseMessage
        {
            Name = "test"
        };
        msg.SetExtension(MessageWithExtensionsExtensions.StringExtension, "extension_value");

        var jsonSerializerOptions = TestHelper.CreateJsonSerializerOptions();

        // Act
        var serialized = JsonSerializer.Serialize(msg, jsonSerializerOptions);
        _output.WriteLine($"Serialized: {serialized}");

        // Assert
        Assert.Contains("\"name\":\"test\"", serialized.Replace(" ", ""));
        // Note: Extensions are not yet supported, so this will fail:
        Assert.Contains("stringExtension", serialized);
        Assert.Contains("\"[stringExtension]\":\"extension_value\"", serialized);
    }

    [Fact]
    public void Should_serialize_message_with_int_extension()
    {
        // Arrange
        var msg = new BaseMessage
        {
            Name = "test"
        };
        msg.SetExtension(MessageWithExtensionsExtensions.IntExtension, 42);

        var jsonSerializerOptions = TestHelper.CreateJsonSerializerOptions();

        // Act
        var serialized = JsonSerializer.Serialize(msg, jsonSerializerOptions);
        _output.WriteLine($"Serialized: {serialized}");

        // Assert
        Assert.Contains("\"name\":\"test\"", serialized.Replace(" ", ""));
        // Note: Extensions are not yet supported, so this will fail:
        Assert.Contains("intExtension", serialized);
        Assert.Contains("\"[intExtension]\":42", serialized);
    }

    [Fact]
    public void Should_serialize_message_with_complex_extension()
    {
        // Arrange
        var msg = new BaseMessage
        {
            Name = "test"
        };
        msg.SetExtension(MessageWithExtensionsExtensions.ComplexExtension, new ExtendedData
        {
            Value = "complex_value",
            Count = 100
        });

        var jsonSerializerOptions = TestHelper.CreateJsonSerializerOptions();

        // Act
        var serialized = JsonSerializer.Serialize(msg, jsonSerializerOptions);
        _output.WriteLine($"Serialized: {serialized}");

        // Assert
        Assert.Contains("\"name\":\"test\"", serialized.Replace(" ", ""));
        // Note: Extensions are not yet supported, so this will fail:
        Assert.Contains("[complexExtension]", serialized);
        Assert.Contains("\"value\":\"complex_value\"", serialized.Replace(" ", ""));
        Assert.Contains("\"count\":100", serialized.Replace(" ", ""));
    }

    [Fact]
    public void Should_serialize_message_with_multiple_extensions()
    {
        // Arrange
        var msg = new BaseMessage
        {
            Name = "test"
        };
        msg.SetExtension(MessageWithExtensionsExtensions.StringExtension, "extension_value");
        msg.SetExtension(MessageWithExtensionsExtensions.IntExtension, 42);
        msg.SetExtension(MessageWithExtensionsExtensions.ComplexExtension, new ExtendedData
        {
            Value = "complex_value",
            Count = 100
        });

        var jsonSerializerOptions = TestHelper.CreateJsonSerializerOptions();

        // Act
        var serialized = JsonSerializer.Serialize(msg, jsonSerializerOptions);
        _output.WriteLine($"Serialized: {serialized}");

        // Assert
        Assert.Contains("\"name\":\"test\"", serialized.Replace(" ", ""));
        // Note: Extensions are not yet supported, so this will fail:
        Assert.Contains("\"[stringExtension]\":\"extension_value\"", serialized);
        Assert.Contains("\"[intExtension]\":42", serialized);
        Assert.Contains("[complexExtension]", serialized);
        Assert.Contains("\"value\":\"complex_value\"", serialized.Replace(" ", ""));
        Assert.Contains("\"count\":100", serialized.Replace(" ", ""));
    }

    [Fact]
    public void Should_deserialize_message_with_extensions()
    {
        // Arrange
        var msg = new BaseMessage
        {
            Name = "test"
        };
        msg.SetExtension(MessageWithExtensionsExtensions.StringExtension, "extension_value");
        msg.SetExtension(MessageWithExtensionsExtensions.IntExtension, 42);

        var jsonSerializerOptions = TestHelper.CreateJsonSerializerOptions();

        // Act
        var serialized = JsonSerializer.Serialize(msg, jsonSerializerOptions);
        _output.WriteLine($"Serialized: {serialized}");
        var deserialized = JsonSerializer.Deserialize<BaseMessage>(serialized, jsonSerializerOptions);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(msg.Name, deserialized.Name);
        Assert.Equal(
            msg.GetExtension(MessageWithExtensionsExtensions.StringExtension),
            deserialized.GetExtension(MessageWithExtensionsExtensions.StringExtension)
        );
        Assert.Equal(
            msg.GetExtension(MessageWithExtensionsExtensions.IntExtension),
            deserialized.GetExtension(MessageWithExtensionsExtensions.IntExtension)
        );
    }

    [Fact]
    public void Should_deserialize_message_with_complex_extension()
    {
        // Arrange
        var msg = new BaseMessage
        {
            Name = "test"
        };
        var extensionData = new ExtendedData
        {
            Value = "complex_value",
            Count = 100
        };
        msg.SetExtension(MessageWithExtensionsExtensions.ComplexExtension, extensionData);

        var jsonSerializerOptions = TestHelper.CreateJsonSerializerOptions();

        // Act
        var serialized = JsonSerializer.Serialize(msg, jsonSerializerOptions);
        _output.WriteLine($"Serialized: {serialized}");
        var deserialized = JsonSerializer.Deserialize<BaseMessage>(serialized, jsonSerializerOptions);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(msg.Name, deserialized.Name);
        var deserializedExtension = deserialized.GetExtension(MessageWithExtensionsExtensions.ComplexExtension);
        Assert.NotNull(deserializedExtension);
        Assert.Equal(extensionData.Value, deserializedExtension.Value);
        Assert.Equal(extensionData.Count, deserializedExtension.Count);
    }
}
