# Overlay 性能探针归档

来源:overlay-dev 交付时的 PerfProbe(真实 OverlayWindow.RenderFrame/Commit 路径,2560×1440,屏外隐藏 ULW 窗口)。

## 结论(M1 验收)
- 全路径 draw+ULW:**avg 1.93ms / p95 2.26ms**(红线 <6ms @2K ✅)
  - Skia 合成 1.04ms + ULW 提交 0.73ms
- 关键优化:遮罩三遍写合并为 SKBlendMode.Src 回形一次写;冻结源 SetFrozen 一次性预乘转存(unpremul 6.2ms/帧 → premul 0.77ms/帧,8 倍差)

## 运行
```
dotnet run --project probe/PerfProbe
```
