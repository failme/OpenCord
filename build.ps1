$env:DOTNET_ROOT = "C:\Users\natha\.dotnet"
& "C:\Users\natha\.dotnet\dotnet.exe" build "$PSScriptRoot\ClaudeScord.csproj" -v:m -nologo @args
