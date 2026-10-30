# 与 Release workflow 相同参数的本地打包脚本：产出 publish/MonitorSwitch.exe
# README 与 release.yml 均引用同一组参数，避免发布配置在多处漂移。
dotnet publish src/MonitorSwitch/MonitorSwitch.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -o publish
