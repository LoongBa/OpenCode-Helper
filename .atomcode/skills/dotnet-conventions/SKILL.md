---
name: dotnet-conventions
description: .NET 10 / C# 项目规范与约定知识库
user_invocable: false
---

# .NET 10 / C# 项目规范

## 技术栈
- **运行时**: .NET 10, C# 13
- **编译模式**: AOT (Ahead-of-Time) — 单文件独立 exe, 无 .NET 运行时依赖
- **目标平台**: Windows (win-x64)
- **UI**: 终端 TUI (字符界面)
- **数据库**: SQLite (Microsoft.Data.Sqlite)

## AOT 编译限制（关键）
- ❌ 禁止使用 `System.Reflection`（运行时反射）
- ❌ 禁止动态加载程序集 (`Assembly.Load`)
- ❌ 禁止 `System.Text.Json` 未经 Source Generator 配置的序列化
- ❌ 禁止 `System.Linq.Expressions` 运行时表达式编译
- ✅ 使用 Source Generators 代替反射
- ✅ 使用 `JsonSerializerContext` 生成 JSON 序列化代码
- ✅ 使用 compile-time 静态分析友好的设计模式

## 项目结构
```
OpenCode-Helper/
├── src/
│   ├── OpenCodeHelper.Cli/          # 入口项目 (AOT 发布)
│   ├── OpenCodeHelper.Core/         # 核心业务逻辑
│   ├── OpenCodeHelper.Data/         # SQLite 数据访问层
│   └── OpenCodeHelper.Tui/          # 终端 UI 组件
├── tests/
│   └── OpenCodeHelper.Tests/        # 单元/集成测试
└── docs/                            # 文档
```

## 命名规范
- **命名空间**: `OpenCodeHelper.*`（PascalCase）
- **类/方法/属性**: PascalCase
- **参数/局部变量**: camelCase
- **接口**: `I` 前缀 (如 `ISessionRepository`)
- **异步方法**: Async 后缀 (如 `GetSessionsAsync`)
- **私有字段**: `_camelCase` 前缀下划线

## 数据库规范
- 使用参数化查询，禁止 SQL 字符串拼接
- 数据库连接字符串不硬编码（使用配置或环境变量）
- Repository 模式封装数据访问

## 错误处理
- 使用 `Result<T>` 模式而非异常控制业务流
- 数据库操作失败需友好提示用户，不可直接暴露异常堆栈
