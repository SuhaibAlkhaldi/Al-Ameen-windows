# Company DLP Windows Ready v1.0.8

- Fixed Mock Server HTTP JSON binding for string enum values such as `"Block"`.
- Added detailed backend HTTP error response logging.
- Fixed empty event display in `SHOW_DEVELOPMENT_EVENTS.bat`.
- Included the v1.0.6 compile/test fixes (`HashSet<int>` and `using Xunit;`).
- Pending encrypted outbox events are retried automatically after restart.
