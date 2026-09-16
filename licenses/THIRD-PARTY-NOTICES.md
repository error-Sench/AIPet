# 第三方素材与依赖许可（分发包须随附本文件）

> 本项目本体以 **GPL-3.0** 发布（见 `LICENSE`）。分发二进制时请同时提供对应源码或获取方式（GPL-3.0 义务）。
> 下面列出随包分发的第三方内容及其许可；各条目按「名称 / 用途 / 许可 / 来源」给出。

## 1. 美术素材

| 名称 | 用途 | 许可 | 来源 |
|---|---|---|---|
| VPet（虚拟桌宠模拟器）动画素材 | `mods/main_anim/anim/loris/**`（待机/走动/思考/睡眠/摸摸/工作/贴边隐藏等全部角色帧） | **Apache License 2.0** | https://github.com/LorisYounger/VPet （Steam 版 `mod/0000_core/pet/vup/`） |

> ⚠ 曾误记为 GPL-3.0，实为 **Apache-2.0**（见 `document/打包审计.md` §A6）。Apache-2.0 允许再分发，
> 但需保留版权与许可声明（本文件即承担该作用），且**不得暗示原作者背书**。

## 2. 字体

| 名称 | 用途 | 许可 | 来源 |
|---|---|---|---|
| LXGW ZhenKai GB（霞鹜臻楷） | `font/LXGWZhenKaiGB-Regular.ttf`（UI 默认字体） | **SIL Open Font License 1.1** | https://github.com/lxgw/LxgwZhenKai |

## 3. 运行时依赖（NuGet，随 .NET 导出产物分发）

| 包 | 用途 | 许可 |
|---|---|---|
| `org.k2fsa.sherpa.onnx` | 语音唤醒（KWS） | Apache-2.0 |
| `System.Speech` | 语音输出（调用 Windows 自带语音，**不内置语音库**） | MIT |
| `Newtonsoft.Json` | JSON | MIT |
| `CsvHelper` | CSV | Apache-2.0 / MS-PL |
| `Facepunch.Steamworks` | Steam 集成（若保留 Steam 层则随包含 `steam_api64.dll`） | MIT + Valve Steamworks SDK 条款 |

## 4. 引擎

| 名称 | 许可 |
|---|---|
| Godot Engine 4.7.2（.NET 版） | MIT（导出产物含其运行时） |
| .NET Runtime | MIT |

---

**分发者注意**：若你在此基础上二次发布，请保留本文件与 `LICENSE`，并在你的发行说明中保留上述署名。
