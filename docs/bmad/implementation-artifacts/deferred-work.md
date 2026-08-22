- source_spec: `docs/bmad/implementation-artifacts/spec-1-2-test-projects-and-rebuild-solution-filter.md`
  summary: Stories 2.1/3.1/4.1/5.1 must remove their test project's Threshold=0 override (and 4.1/5.1 re-add ProjectReference+Include) — enforce in those story specs.
  evidence: TODO markers in test csprojs are the only in-code record; review flagged the obligation as easy to lose when those specs are drafted.
- source_spec: `docs/bmad/implementation-artifacts/spec-3-1-validated-processing-options.md`
  summary: CoreBankDemo.PaymentsAPI/appsettings.json and CoreBankDemo.CoreBankAPI/appsettings.json still set PartitionCount:2 (should be 4, ruling A3) and still contain LockRenewIntervalSeconds keys (dead, ruling A4) — epics 4/5 must fix these when rebuilding those projects' config, not just the C# option types.
  evidence: Story 3.1 review found the C# default/removal is correct but the live config files that will actually be bound at runtime were untouched (out of story 3.1's Configuration/-only scope); those projects are currently unbuilt/off-slnf so it's not an active regression, but must not be forgotten when epics 4/5 rebuild them.
