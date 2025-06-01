cd /d "%~dp0"

dotnet pack .\src\ManagedShell\ManagedShell.csproj -c Release ^
 -p:AssemblyOriginatorKeyFile=d:\cert\cert2022\quicker.snk ^
 -p:SignAssembly=true ^
 -o ..\_nupkgs

 pause
