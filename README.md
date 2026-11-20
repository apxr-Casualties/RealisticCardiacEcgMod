# Realistic Cardiac & ECG Mod (V1.0.0)

为 Unity 游戏 *Casualties Unknown* 打造的多线程二维心肌网格电生理仿真与真实心电图大修 Mod（基于 BepInEx 5 + Harmony + CUCoreLib）。

## 核心特性

### 1. 二维心肌细胞电生理仿真内核（`CardiacSimCore`）
- **分区解剖网格**：`72×72` 额面网格（1178 个有效细胞单元），划分窦房结（SA）、左右心房、房室结（AV）、希浦系统（Purkinje）、室间隔与左右心室，模拟细胞间动作电位扩布、不应期离散与复极梯度。
- **独立后台工作线程**：通过 `CardiacWorkerRunner` 将 `1000 Hz`（1 ms 步长）计算卸载至独立 CPU 核心，支持游戏最高 `20x–25x` 时间加速，主线程平均耗时仅 `~0.01 ms/帧`。
- **真实机械射血替代心率算血压**：由左/右心室心肌钙瞬变积分计算每搏输出量（LV/RV Stroke Volume）与心输出量，在室颤/停搏时搏出量归零导致循环衰竭与低血压。
- **分级心律失常演进**：依据血氧/血压平滑缓冲的心肌缺血值，以及钙过载应激，依次演化为 **窦性心律 → 孤立性室性早搏（PVC） → 室性心动过速（VT） → 粗颤/细颤（VF） → 能量耗竭后停搏（Asystole）**。
- **自动泵 / 心肺复苏机制**：当 `bloodPressure > 60` 且 `bloodOxygen > 50` 时逐步恢复心肌 ATP 储备，实现心肺复苏下 **停搏 → 小纤颤 → 大纤颤** 的抢救窗口，配合除颤恢复窦性心律。

### 2. 药代动力学与双轨儿茶酚胺系统
- **胺碘酮（Amiodarone）**：约 `300s` 游戏时间半衰期，延长心肌 APD/ERP、减慢 AV 传导并降低室颤阈值。
- **利多卡因（Lidocaine）**：通过 `CUCoreLib` 注册同名液体（`200/L`，`10 mL = 100 mg`，`120s` 半衰期）及合成配方（`50 mL localanesthetic + 10 mL biochem -> 25 mL lidocaine`，`INT >= 8`），强力抑制缺血或儿茶酚胺过载引起的室早/室速。
- **双轨肾上腺素**：保留受击分泌与兴奋剂的内源上限（`100`）；注射肾上腺素（`epinephrine`）先补足内源至 `100`，溢出部分叠加进入**无上限外源性肾上腺素**，显著加快房室结与心室传导速度，过量引发心肌钙过载与需求性缺血。

### 3. 全界面高清心电显示、同步切导联与六导联心电图仪
- **高分辨率抗锯齿示波渲染**：芯片医疗面板、昏迷监控与手动除颤仪均采用 `3x` 超采样双线性渲染与帧率无关余辉衰减；支持 `25x` 昏迷高倍速下自动切换可读实时条带回放，并解锁昏迷/濒死倍速按键控制。
- **同步导联切换与存档绑定**：芯片面板心电图右侧及手动除颤仪均提供 `I / II / III / aVR / aVL / aVF` 导联切换按钮，切换即清空旧轨迹；导联设置及心肌状态/药物浓度通过 `CUCoreLib` 随存档持久化。
- **六导联标准网格心电图小游戏**：
  - 持芯片时按可改建热键（默认 `G`，可在游戏设置中修改）开关；或使用新物品 **便携式心电图仪（`ecgmonitor`，`0.1u`，医疗箱概率掉落）** 无视芯片直接开启。
  - 标准校准网格（`25 mm/s`、`10 mm/mV` 下横向小格 `0.04s` / 大格 `0.20s`，纵向小格 `0.1mV` / 大格 `0.5mV`），支持从左向右扫描刷新、热切换**走纸速度（12.5 / 25 / 50 mm/s）**与**增益（5 / 10 / 20 mm/mV）**且保留历史曲线，提供**冻结测量**与实时心律识别。

---

## 目录结构

- `CardiacSimCore/`：零 Unity 依赖的心肌网格电生理、药代动力学与多线程 Worker 核心库（`netstandard2.1`）。
- `RealisticEcgMod/`：BepInEx 5 插件本体、Harmony 补丁、`CUCoreLib` 物品/液体/配方/选项/存档集成、UI 与六导联小游戏。
- `CardiacSimVerifier/`：离线控制台验证与性能基准程序（验证 14 组生理/病理场景及 20 倍速线程性能）。

---

## 安装要求（玩家）

1. 安装 [BepInEx 5](https://github.com/BepInEx/BepInEx)（Mono 版本）。
2. 安装硬前置模组 **CUCoreLib**（v1.1+）。
3. 将 Release 构建或发布包中的 `CardiacSimCore.dll` 与 `RealisticEcgMod.dll` 放入 `BepInEx/plugins/` 目录即可。

---

## 从源码编译（开发者）

1. 复制根目录下的 `Directory.Build.props.example` 为 `Directory.Build.props`，填入本地 *Casualties Unknown* 游戏目录中的 `BepInEx/core`、`Managed` 与 `CUCoreLib.dll` 路径。
2. 运行：
   ```bash
   dotnet build RealisticEcgMod.sln -c Release
   ```
3. 运行离线仿真与 20x 性能验证：
   ```bash
   dotnet run --project CardiacSimVerifier/CardiacSimVerifier.csproj -c Release
   ```

---

## 开源协议

本项目基于 [MIT License](LICENSE) 开源。
