// AssemblyAttributes.cs — disable test-class parallelism for the
// Zeus.PluginHost.Tests assembly. Both SidecarLifecycleTests and
// RoundTripTests spawn sidecar processes named "zeus-plughost", and each
// class's teardown enumerates Process.GetProcessesByName("zeus-plughost")
// to kill orphans. If the two classes run in parallel, one class's
// teardown will see the other class's still-running sidecar and treat it
// as a leak. Serializing across the whole assembly keeps each class's
// teardown unambiguous.

using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
