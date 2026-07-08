# Test

**命令**: `/test`
**描述**: 运行所有单元测试和集成测试

```bash
dotnet test
```

## 带覆盖率
```bash
dotnet test --collect:"XPlat Code Coverage"
```

## 仅运行特定测试
```bash
dotnet test --filter "Category=Unit"
dotnet test --filter "FullyQualifiedName~SessionRepository"
```
