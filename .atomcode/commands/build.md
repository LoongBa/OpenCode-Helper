# Build

**命令**: `/build`
**描述**: 构建并发布 AOT 编译的单文件 exe

```bash
dotnet publish src/OpenCodeHelper.Cli -c Release -r win-x64 --self-contained -p:PublishAot=true -p:StripSymbols=true
```

输出路径: `src/OpenCodeHelper.Cli/bin/Release/net10.0/win-x64/publish/OpenCodeHelper.exe`

## 调试构建
```bash
dotnet build src/OpenCodeHelper.Cli
```
