# Vendored TianshuDM Source

The Studio is an independent product. It does not reference the TianshuDM solution or
projects from their original location. Selected source was copied into this repository
for independent evolution.

## Source

```text
Repository: E:\VP\Unity\RTS_DR\trunk\其他\配置
Commit:     d8561e045fdde002c5c1b10b28ff2f8e706a7780
Date:       2026-09-20 11:58:47 +0800
```

## Copied Projects

```text
TianshuDM.Contract
TianshuDM.Domain
TianshuDM.Application
TianshuDM.Infrastructure.Excel
TianshuDM.Infrastructure.Sqlite
```

These projects were copied without `bin`, `obj`, `.git`, and IDE metadata. Their
relative project references were preserved inside `vendor/TianshuDM`.

## Not Copied

```text
TianshuDM.Api
TianshuDM.Infrastructure.Luban
packaging/
web/
tests/
```

The Studio will add its own API, Agent, Plan, compiler, validation, workbook diff,
export, and frontend projects. TianshuDM API routes and product shell are not part of
the Studio.

## Upgrade Policy

Upstream changes are not pulled automatically. When a fix is needed, inspect the
upstream diff, copy only the relevant change, and record the reason in the Studio
changelog.
