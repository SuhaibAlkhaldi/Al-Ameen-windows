# Company DLP Windows Ready v1.0.5

- Creates launcher logs before restore/build so startup can no longer fail silently.
- Adds explicit step markers around build, runtime validation, Mock Server, Service, shell integration, and Desktop startup.
- Starts Mock Server and Service before File Explorer context-menu registration.
- Adds detailed top-level PowerShell error capture in `.development/logs/launcher.error.log`.
- Adds a transcript in `.development/logs/launcher.transcript.log`.
- Adds `START_DEVELOPMENT_CORE_ONLY.bat` to test Mock Server, Service, and Desktop independently of shell integration.
- Preserves Smart App Control compatibility by launching all .NET components through signed `dotnet.exe`.
