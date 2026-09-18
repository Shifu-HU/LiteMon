@echo off
REM LiteMon 一键构建脚本（需要 .NET 8 SDK）
setlocal
cd /d "%~dp0"
echo [1/3] 还原依赖...
dotnet restore LiteMon.sln || goto :fail
echo [2/4] 构建（Release）...
dotnet build LiteMon.sln -c Release || goto :fail
echo [3/4] 运行测试...
dotnet test tests\LiteMon.Core.Tests\LiteMon.Core.Tests.csproj -c Release || goto :fail
echo [4/4] 打包单文件 exe...
dotnet publish src\LiteMon.Wpf\LiteMon.Wpf.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -o publish || goto :fail
echo.
echo 构建完成: publish\LiteMon.exe （单文件绿色版）
echo       开发版: src\LiteMon.Wpf\bin\Release\net8.0-windows\LiteMon.exe
exit /b 0
:fail
echo 构建失败
exit /b 1
