# HttpLogger

使用 C# 编写的简易 HTTP 中间人，用于记录所有 HTTP 请求和响应。

此项目的目的是帮助开发者理解和观察 AI 智能体调用工具时发生了什么。

### 使用方法

```shell
./HttpLogger <本地地址> <本地端口> <远程地址> <远程端口> [--ignore-ssl / -k] [--output <日志输出路径>]
```

示例：

```shell
./HttpLogger 127.0.0.1 8080 api.siliconflow.cn 443
```

> 具体参数请查看 `Program.cs`；

### 温馨提示

本工具几乎完全由 AI 编写，如需使用请留意代码质量。
