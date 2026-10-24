# Kavita.Importer

`Kavita.Importer` 是一个命令行工具，用于把书籍文件导入现有 Kavita 实例的 SQLite 数据库。它复用 Kavita 的解析、分组和元数据处理逻辑，适合在批量迁移或服务器扫描前导入已有文件。

## 使用前准备

1. 停止 Kavita 服务，避免 SQLite 数据库锁冲突。
2. 备份 Kavita 数据目录，至少备份 `config/kavita.db`。
3. 确认 `--kavita-dir` 指向 Kavita 服务器根目录。该目录必须包含 `config/kavita.db`。
4. `--dir` 应位于目标 Library 配置的扫描目录下。否则后续 Kavita 扫描时可能将这些文件视为不属于该 Library。

Importer 会直接写入数据库，不会移动或复制书籍文件。请确保运行 Importer 的用户对书籍目录和 Kavita `config` 目录具有读写权限。

## 基本用法

```bash
./Kavita.Importer \
  --kavita-dir /srv/kavita \
  --dir /data/books \
  --library-name Manga
```

也可以使用 Library ID：

```bash
./Kavita.Importer \
  --kavita-dir /srv/kavita \
  --dir /data/books \
  --library-id 2
```

`--kavita-dir` 默认为当前工作目录。使用容器中的 Importer 时，通常需要把 Kavita 的服务器目录和书籍目录挂载到容器，并将路径传给对应参数。

## 命令行参数

| 参数 | 说明 |
| --- | --- |
| `--kavita-dir <path>` | Kavita 服务器根目录，必须包含 `config/kavita.db`；默认是当前目录。 |
| `--dir <path>` | 要导入的书籍根目录；普通导入时必填。 |
| `--library-id <id>` | 按数据库 ID 指定目标 Library。 |
| `--library-name <name>` | 按名称指定目标 Library。不能和 `--library-id` 同时使用。 |
| `--dir-as-serial` | 使用每本书所在文件夹名称作为系列名，忽略目录层级的默认解析规则。 |
| `--force-update` | 强制重新生成封面和字数统计，效果接近服务器重新扫描。 |
| `--no-covers` | 跳过封面生成。 |
| `--no-wordcount` | 跳过字数统计。 |
| `--dry-run` | 只解析文件并显示分组结果，不写入数据库。建议首次导入先使用该选项。 |
| `--list-libraries` | 列出数据库中的 Library。使用此选项时不需要 `--dir` 或 Library 参数。 |
| `--verbose`、`-v` | 输出调试日志。 |
| `--help`、`-h` | 显示帮助。 |

必须提供 `--library-id` 或 `--library-name` 其中一个。`--library-id` 和 `--library-name` 互斥。

## 常用流程

先列出 Library，确认名称和 ID：

```bash
./Kavita.Importer --kavita-dir /srv/kavita --list-libraries
```

首次导入先预览分组结果：

```bash
./Kavita.Importer \
  --kavita-dir /srv/kavita \
  --dir /data/books \
  --library-name Manga \
  --dry-run \
  --verbose
```

确认输出后执行实际导入：

```bash
./Kavita.Importer \
  --kavita-dir /srv/kavita \
  --dir /data/books \
  --library-name Manga
```

如果文件夹名称就是系列名称，可以使用：

```bash
./Kavita.Importer \
  --kavita-dir /srv/kavita \
  --dir /data/books \
  --library-name Manga \
  --dir-as-serial
```

大批量导入时，如果暂时不需要生成封面或统计字数，可以使用：

```bash
./Kavita.Importer \
  --kavita-dir /srv/kavita \
  --dir /data/books \
  --library-id 2 \
  --no-covers \
  --no-wordcount
```

## 构建和发布

在仓库根目录执行：

```bash
dotnet publish Kavita.Importer/Kavita.Importer.csproj \
  -c Release \
  --self-contained \
  --runtime linux-x64 \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o _output/importer-linux-x64
```

生成的可执行文件为：

```text
_output/importer-linux-x64/Kavita.Importer
```

将 `linux-x64` 替换为目标 RID 可以生成其他平台版本，例如 `linux-arm64`。仓库中的 `build.sh` 和 `docker-build.sh` 会自动生成对应的单文件 Importer；Docker 镜像中的路径为 `/kavita/Kavita.Importer`。

## 常见问题

**找不到数据库文件**

请确认 `--kavita-dir` 是 Kavita 服务器根目录，而不是 `config` 目录。正确结构应类似：

```text
/srv/kavita/
├── Kavita
└── config/
    └── kavita.db
```

**导入后服务器扫描不到文件**

确认 `--dir` 位于目标 Library 的扫描文件夹中，并检查 Library 的文件类型设置和排除规则。

**数据库被锁定**

停止 Kavita 服务后重新运行，并确认没有其他 Kavita 进程正在使用 `config/kavita.db`。

**想先确认会导入什么**

使用 `--dry-run`。该模式会解析和显示分组结果，但不会写入数据库。

