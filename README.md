# PowerPulse

Windows 台式机实时耗电量统计工具。仅读取真实硬件功耗数据（无任何估算模式），单文件 exe、零运行时依赖，界面仿照 [Pavise](https://github.com/felipe-fg/Pavise) 风格重设计。

## 功能

- **真实功耗读数**
  - GPU：NVIDIA NVAPI 实时功耗
  - CPU：Intel RAPL（经 WinRing0 驱动读取 MSR）或 Intel XTU 遥测，二者取可用者
  - 无法读取时显示占位符，绝不用估算值冒充
- **按开机会话统计**：每次开机记录一条汇总（累计度数、平均/峰值功率），无高频明细刷屏
- **每日统计页**：今日/本月累计、近 7 日日均、近 14 日耗电柱状图（由会话明细按自然日积分，跨零点自动切分）
- **实时曲线**：总功率/CPU/GPU 三曲线，悬停查看任意时刻数值，图例点击切换显隐
- **托盘常驻**：开机自启（计划任务/Run 键），托盘悬停即显示当前功率，关闭窗口最小化到托盘
- **高 DPI 适配**：PerMonitorV2 感知，150%/200% 缩放下界面清晰
- **单实例**：重复启动自动唤起已有窗口

## 隐私

所有数据仅保存在本地 `data\` 目录（CSV + 日志），程序无任何网络访问。

## 构建

无需安装任何 SDK，使用系统自带的 .NET Framework 4 编译器：

```bat
build.cmd
```

产物为 `PowerPulse.exe`（图标经 `/win32icon` 嵌入，同时以资源形式内嵌供托盘/窗口使用）。

品牌图标由 `tools\IconGen.cs` 本地生成（红色心电脉冲波形，非闪电）：

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /out:tools\IconGen.exe tools\IconGen.cs
tools\IconGen.exe src\app.ico
```

## 运行

以**管理员身份**运行 `PowerPulse.exe`（RAPL/XTU 读取硬件 MSR 必需）。首次运行会自动注册开机自启。

可选：安装 [Intel Extreme Tuning Utility](https://www.intel.com/content/www/us/en/download/19706/intel-extreme-tuning-utility.html) 以获得更稳定的 CPU 功耗读数（未安装且 RAPL 不可用时 CPU 项显示占位符）。

## 目录结构

```
src\       全部 C# 源码与资源(app.ico / app.manifest)
driver\    WinRing0x64.sys(第三方开源驱动, 见下)
tools\     IconGen.cs 图标生成器
data\      运行时生成, 不入库
build.cmd  一键编译脚本
```

## 第三方组件

- `driver\WinRing0x64.sys`：WinRing0 内核驱动（作者 Michal Mularczyk，GPL 开源），随 [Open Hardware Monitor](https://github.com/openhardwaremonitor/openhardwaremonitor) 项目发布，用于读取 CPU MSR。程序运行时自动注册/卸载该驱动，不留系统目录残留。
- NVAPI：NVIDIA 图形驱动自带的公开头文件接口，运行时动态加载 `nvapi.dll`，不随本仓库分发。

## 许可证

MIT（见 [LICENSE](LICENSE)）。`driver\` 下的 WinRing0 驱动单独适用其 GPL 条款。
