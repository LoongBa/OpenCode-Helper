using System.Text.Json.Serialization;

namespace OpenCodeHelper.Models;

/// <summary>用于 AOT 编译的 JSON 序列化上下文（源生成）</summary>
[JsonSerializable(typeof(AppConfig))]
internal partial class AppConfigJsonContext : JsonSerializerContext
{
}
