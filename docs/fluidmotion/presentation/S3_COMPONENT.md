# 组件接入

组件源固定MF44401d3f002b427d828500331a3e9b4ada5881cd，FluidMotion.L1精确PackageReference Version="[0.1.0]"，net8.0/win-x64；命名空间仍为MotionSoftDemo。每pipeline仅构建一次，Terminal发布、安装器、独立消费检查和交付复用同一nupkg；以新交付manifest的SHA为准，不能把旧0.9.2的b2e47包SHA当作本次新构建必须相等。

把component/feed作为本地NuGet源，NuGet.config中FluidMotion.L1仅映射到该源；既有第三方依赖仍映射到正常源。为本次应用使用新的NUGET_PACKAGES目录，避免相同0.1.0历史candidate缓存。保留build-input、component-state、framework-result和consumption收据；不要手工复制旧全局DLL。程序消费托管库与runtimes/win-x64/native资产及token；实际加载路径/SHA由一次性CI安装验收核对。

本轮不更改MF公共面、native源码或发布公开NuGet。组件订阅、帧泵与句柄生命周期由私有UI适配器管理，减少动画偏好只读；没有性能/采纳认证结论。
