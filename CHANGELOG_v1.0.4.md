# Company DLP Windows Ready v1.0.4

- Fixed Development startup under Smart App Control.
- Mock Server and Windows Service now run through the signed `dotnet.exe` host using their DLLs instead of `dotnet run`/unsigned apphost executables.
- Added explicit startup status messages for Mock Server, Service, and Desktop.
- Added `.development/logs` stdout/stderr capture for Mock Server and Service.
- Added early process-exit detection and actionable error output.
- Updated standalone Mock Server launcher to use the DLL path.
