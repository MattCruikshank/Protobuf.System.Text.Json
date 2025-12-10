using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Protobuf.System.Text.Json.InternalConverters;

namespace Protobuf.System.Text.Json;

internal class ProtobufConverter<T> : JsonConverter<T?> where T : class, IMessage, new()
{
    private readonly FieldInfo[] _fields;
    private readonly Dictionary<string, FieldInfo> _fieldsLookup;
    private readonly JsonIgnoreCondition _defaultIgnoreCondition;

    public ProtobufConverter(JsonSerializerOptions jsonSerializerOptions, JsonProtobufSerializerOptions jsonProtobufSerializerOptions)
    {
        _defaultIgnoreCondition = jsonSerializerOptions.DefaultIgnoreCondition;

        var type = typeof(T);
        
        var propertyTypeLookup = type.GetProperties().ToDictionary(x => x.Name, x => x.PropertyType);

        var propertyInfo = type.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static);
        var messageDescriptor = (MessageDescriptor) propertyInfo?.GetValue(null, null)!;
        
        var convertNameFunc = GetConvertNameFunc(jsonSerializerOptions.PropertyNamingPolicy, jsonProtobufSerializerOptions.PropertyNamingSource);

        _fields = messageDescriptor.Fields.InDeclarationOrder().Select(fieldDescriptor =>
        {
            var enumType = jsonProtobufSerializerOptions.UseStringProtoEnumValueNames && fieldDescriptor.FieldType == FieldType.Enum
                ? fieldDescriptor.EnumType
                : null;
            var fieldInfo = new FieldInfo
            {
                Accessor = fieldDescriptor.Accessor,
                IsRepeated = fieldDescriptor.IsRepeated,
                EnumType = enumType,
                IsMap = fieldDescriptor.IsMap,
                FieldType = FieldTypeResolver.ResolverFieldType(fieldDescriptor, propertyTypeLookup),
                JsonName = convertNameFunc(fieldDescriptor),
                IsOneOf = fieldDescriptor.ContainingOneof != null,
            };
            fieldInfo.Converter = InternalConverterFactory.Create(fieldInfo, jsonSerializerOptions);
            return fieldInfo;
        }).ToArray();

        var stringComparer = jsonSerializerOptions.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _fieldsLookup = _fields.ToDictionary(x => x.JsonName, x => x, stringComparer);
    }

    private static Func<FieldDescriptor, string> GetConvertNameFunc(JsonNamingPolicy? jsonNamingPolicy, PropertyNamingSource propertyNamingSource)
    {
        switch (propertyNamingSource)
        {
            case PropertyNamingSource.ProtobufJsonName:
                return descriptor => descriptor.JsonName;
            
            case PropertyNamingSource.ProtobufFieldName:
                return descriptor => descriptor.Name;
            
            case PropertyNamingSource.Default:
            default:
                if (jsonNamingPolicy != null)
                {
                    return descriptor => jsonNamingPolicy.ConvertName(descriptor.PropertyName);
                }
                return descriptor => descriptor.PropertyName;
        }
    }

    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"The JSON value could not be converted to {typeToConvert}.");
        }
        
        var obj = new T();

        // Process all properties.
        while (true)
        {
            // Read the property name or EndObject.
            reader.Read();

            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                reader.Read();
                continue;
            }

            var propertyName = reader.GetString();

            if (propertyName == null)
            {
                reader.Read();
                continue;
            }

            // Check if this is an extension (property name starts with '[')
            if (propertyName.StartsWith("[") && propertyName.EndsWith("]"))
            {
                // This is an extension field
                ReadExtension(ref reader, obj, propertyName, options);
                continue;
            }

            if (!_fieldsLookup.TryGetValue(propertyName, out var fieldInfo))
            {
                // We need to call TrySkip instead of Skip as Skip may throw exception when called in DeserializeAsync
                // context https://github.com/dotnet/runtime/issues/39795
                _ = reader.TrySkip();
                continue;
            }

            reader.Read();
            fieldInfo.Converter.Read(ref reader, obj, fieldInfo.FieldType, options, fieldInfo.Accessor);
        }

        return obj;
    }

    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();

        foreach (var fieldInfo in _fields)
        {
            if (fieldInfo.IsOneOf && fieldInfo.Accessor.HasValue(value) == false)
            {
                continue;
            }

            var obj = fieldInfo.Accessor.GetValue(value);
            if (obj is { } propertyValue)
            {
                if (_defaultIgnoreCondition is JsonIgnoreCondition.Never or not JsonIgnoreCondition.WhenWritingDefault)
                {
                    writer.WritePropertyName(fieldInfo.JsonName);
                    fieldInfo.Converter.Write(writer, propertyValue, options);
                }
            }
            else if (obj is null && _defaultIgnoreCondition == JsonIgnoreCondition.Never)
            {
                writer.WritePropertyName(fieldInfo.JsonName);
                writer.WriteNullValue();
            }
        }

        // Write extensions if the message supports them
        WriteExtensions(writer, value, options);

        writer.WriteEndObject();
    }

    private void WriteExtensions(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        // Check if this message type supports extensions (implements IExtendableMessage)
        if (value is not IMessage message)
        {
            return;
        }

        // Get the message descriptor
        var descriptor = message.Descriptor;

        // Find all extension fields registered for this message type
        // Extensions are registered in the file descriptor
        // Check dependencies first, then the file itself
        var filesToCheck = new List<FileDescriptor>();
        foreach (var dep in descriptor.File.Dependencies)
        {
            filesToCheck.Add(dep);
        }
        filesToCheck.Add(descriptor.File);

        foreach (var file in filesToCheck)
        {
            foreach (var extension in file.Extensions.UnorderedExtensions)
            {
                // Note: extension.ContainingType is null for file-level extensions
                // We'll try to get the value for all extensions and skip if not set
                if (extension == null)
                {
                    continue;
                }

                // Use the GetExtension method on the message
                // We need to find the static Extension<TMessage, TValue> field
                // Look in the file's reflection class for extension fields
                var extensionField = FindStaticExtensionField(extension);
                if (extensionField == null)
                {
                    continue;
                }

                try
                {
                    // Call message.GetExtension(extension) - we use the generic version
                    // The method signature is: TValue GetExtension<TValue>(Extension<TMessage, TValue> extension)
                    var extensionType = extensionField.GetType();
                    if (!extensionType.IsGenericType)
                    {
                        continue;
                    }

                    var genericArgs = extensionType.GetGenericArguments();
                    if (genericArgs.Length != 2)
                    {
                        continue;
                    }

                    var messageType = genericArgs[0]; // Should be T
                    var valueType = genericArgs[1];   // The actual value type

                    // Get the generic GetExtension method
                    MethodInfo? getExtensionMethod = null;
                    foreach (var method in typeof(T).GetMethods())
                    {
                        if (method.Name == "GetExtension" && method.IsGenericMethod)
                        {
                            getExtensionMethod = method;
                            break;
                        }
                    }

                    if (getExtensionMethod == null)
                    {
                        continue;
                    }

                    var genericGetExtension = getExtensionMethod.MakeGenericMethod(valueType);
                    var extensionValue = genericGetExtension.Invoke(value, new[] { extensionField });

                    if (extensionValue == null)
                    {
                        continue;
                    }

                    // Check if this is the default value (for primitive types, we might want to skip defaults)
                    // For now, write it anyway

                    // Write the extension in protobuf JSON format: "[extensionName]": value
                    // Use JsonName to get the camelCase version of the field name
                    var extensionJsonName = $"[{extension.JsonName}]";
                    writer.WritePropertyName(extensionJsonName);

                    // Serialize the extension value
                    JsonSerializer.Serialize(writer, extensionValue, valueType, options);
                }
                catch (Exception)
                {
                    // Skip extensions that fail
                    continue;
                }
            }
        }
    }

    private static object? FindStaticExtensionField(FieldDescriptor extension)
    {
        // The extension is stored as a static field in a class named [FileName]Extensions
        // We need to search all loaded assemblies for this
        var extensionName = extension.Name;

        // Try to find the extension in the same assembly as the message type
        var assembly = typeof(T).Assembly;
        foreach (var type in assembly.GetTypes())
        {
            if (!type.Name.EndsWith("Extensions"))
            {
                continue;
            }

            // Look for static fields that are Extension<T, TValue>
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static);
            foreach (var field in fields)
            {
                // Extension<T, TValue> will have a generic type name
                if (!field.FieldType.IsGenericType ||
                    !field.FieldType.Name.Contains("Extension"))
                {
                    continue;
                }

                var extensionObj = field.GetValue(null);
                if (extensionObj == null)
                {
                    continue;
                }

                // Check if this is the right extension by comparing field number
                var fieldNumberProp = extensionObj.GetType().GetProperty("FieldNumber");
                if (fieldNumberProp != null)
                {
                    var fieldNumber = fieldNumberProp.GetValue(extensionObj) as int?;
                    if (fieldNumber == extension.FieldNumber)
                    {
                        return extensionObj;
                    }
                }
            }
        }

        return null;
    }

    private void ReadExtension(ref Utf8JsonReader reader, T message, string propertyName, JsonSerializerOptions options)
    {
        // Extract extension name from "[extensionName]"
        var extensionJsonName = propertyName.Substring(1, propertyName.Length - 2);

        // Find the extension field descriptor by JSON name
        var descriptor = message.Descriptor;
        FieldDescriptor? extensionDescriptor = null;

        var filesToCheck = new List<FileDescriptor>();
        foreach (var dep in descriptor.File.Dependencies)
        {
            filesToCheck.Add(dep);
        }
        filesToCheck.Add(descriptor.File);

        foreach (var file in filesToCheck)
        {
            foreach (var ext in file.Extensions.UnorderedExtensions)
            {
                if (ext.JsonName == extensionJsonName)
                {
                    extensionDescriptor = ext;
                    break;
                }
            }

            if (extensionDescriptor != null)
            {
                break;
            }
        }

        if (extensionDescriptor == null)
        {
            // Unknown extension, skip it
            reader.Read();
            _ = reader.TrySkip();
            return;
        }

        // Find the static Extension field
        var extensionField = FindStaticExtensionField(extensionDescriptor);
        if (extensionField == null)
        {
            // Can't find extension field, skip it
            reader.Read();
            _ = reader.TrySkip();
            return;
        }

        try
        {
            // Get the extension type to determine the value type
            var extensionType = extensionField.GetType();
            if (!extensionType.IsGenericType)
            {
                reader.Read();
                _ = reader.TrySkip();
                return;
            }

            var genericArgs = extensionType.GetGenericArguments();
            if (genericArgs.Length != 2)
            {
                reader.Read();
                _ = reader.TrySkip();
                return;
            }

            var valueType = genericArgs[1];

            // Read the next token (the value)
            reader.Read();

            // Deserialize the value
            var value = JsonSerializer.Deserialize(ref reader, valueType, options);

            if (value == null)
            {
                return;
            }

            // Call message.SetExtension(extension, value)
            MethodInfo? setExtensionMethod = null;
            foreach (var method in typeof(T).GetMethods())
            {
                if (method.Name == "SetExtension" && method.IsGenericMethod)
                {
                    setExtensionMethod = method;
                    break;
                }
            }

            if (setExtensionMethod != null)
            {
                var genericSetExtension = setExtensionMethod.MakeGenericMethod(valueType);
                genericSetExtension.Invoke(message, new[] { extensionField, value });
            }
        }
        catch
        {
            // Skip on error
            _ = reader.TrySkip();
        }
    }
}