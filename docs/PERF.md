# 拖动卡顿的排查记录

问题：窗口拖动/缩放时"一动就等半天才能动到那边"。

## 方法

写 `tools/TestPerf2.cs`，在同样条件下反复改变窗口尺寸（16ms 一次），
测量每次 `UpdateLayout()` 的耗时分布，逐项剥离组件找真凶：

- A：空透明窗口（基线）
- B：只加一张背板 Image，不模糊
- C：Image + BlurEffect(radius 20)
- D：Blur + 每帧换新图（模拟实时采集）
- E：Blur + 尺寸变化时把半径压到 0（冻结）

## 数据（32 核 / Win11 / 200% DPI 环境实测）

| 场景 | 帧数 | p50 | p95 | max | 判定 |
|---|---|---|---|---|---|
| A 空窗口(AllowsTransparency=true) | 44 | 15.6 | 18.9 | 19.7 | OK |
| B 背板Image(无模糊) | 44 | 22.1 | 29.0 | 54.2 | SLOW |
| C Blur radius20 | 40 | 23.9 | 28.3 | 28.8 | SLOW |
| D Blur + 每帧换图 | 44 | 23.4 | 29.9 | 30.7 | SLOW |
| E Blur + 尺寸时冻结 | 43 | 20.7 | 26.8 | 29.0 | SLOW |

## 结论

**BlurEffect 不是瓶颈。** B → C 只差 1~2ms。
只要在 `AllowsTransparency=true` 的窗口里贴一张图，UI 停顿就从 18.9ms 涨到 29ms。
根因是 WPF 在这种窗口下走**软件合成**，整窗重绘成本极高。

所以优化方向不是"少模糊一点"，而是**别走软件合成**。

## 最终方案

把默认渲染改成系统 Acrylic（`ACCENT_ENABLE_ACRYLICBLURBEHIND` + `AllowsTransparency=false`），
由 DWM 在 GPU 上合成，磨砂感用矢量渐变补足。

改后实测：

| 版本 | p50 | p95 | max |
|---|---|---|---|
| 改造前（自绘分层 + 软件合成） | 36.6 | 47.2 | 47.5 |
| 中间尝试（冻结模糊） | 31.2 | 39.4 | 58.1 |
| **最终（系统 Acrylic）** | **17.8** | **22.7** | **27.3** |

p95 降低 52%，最大值降低 43%，已接近空窗口基线（18.9ms）。

## 保留的其它优化

- 采集图用 `StretchBlt` 直接降采样到 200px 边长
- 帧序号比对，没有新帧不重绘
- `WM_ENTERSIZEMOVE` 期间暂停采集并冻结模糊
- 采集线程 `ThreadPriority.BelowNormal`
- 所有动画走 `RenderTransform`（GPU），不用 `LayoutTransform`
