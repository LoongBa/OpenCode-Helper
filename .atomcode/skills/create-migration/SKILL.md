---
name: create-migration
description: 创建 SQLite 数据库迁移脚本
disable_model_invocation: true
---

# Create Migration

创建一个新的 SQLite 数据库迁移脚本。

## Parameters
- `name` (必填): 迁移名称，如 `add-backup-table`
- `description` (可选): 迁移描述

## 模板

在 `src/OpenCodeHelper.Data/Migrations/` 下创建文件，命名格式 `YYYYMMDDHHmmss_{name}.cs`：

```csharp
namespace OpenCodeHelper.Data.Migrations;

public sealed class Migration_{timestamp} : IMigration
{
    public string Version => "{timestamp}";
    public string Description => "{description}";

    public string Up() => @"
-- UP: {description}
CREATE TABLE IF NOT EXISTS {table_name} (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
);
";

    public string Down() => @"
-- DOWN: {description}
DROP TABLE IF EXISTS {table_name};
";
}
```

## 规则
1. 迁移必须可回滚（提供 Up/Down）
2. 迁移文件名 = 版本号 + 描述
3. 新迁移基于当前最新版本递增
4. 使用事务包裹 Up 操作
