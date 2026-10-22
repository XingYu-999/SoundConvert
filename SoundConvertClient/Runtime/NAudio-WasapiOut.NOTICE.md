# NAudio WasapiOut adaptation

`ClockedWasapiOut.cs` derives from NAudio 2.2.1 `NAudio.Wasapi/WasapiOut.cs`:
https://github.com/naudio/NAudio/blob/v2.2.1/NAudio.Wasapi/WasapiOut.cs

Copyright 2020 Mark Heath. Distributed under the MIT license reproduced in
`NAudio-WasapiOut.LICENSE.txt`.

The adaptation keeps the event-driven shared/exclusive render algorithm and
format fallback behavior. Local changes rename the class/namespace, create its
actual render `IAudioClient` through an explicit native factory (also for
exclusive alignment retry), expose that client's clock service to a separate
observer, expose actual buffer/latency values, dispose the event handle, replace
an internal buffer helper with the equivalent capacity check, and clear an
exclusive partial buffer in managed memory before copying it to the render
buffer. The inherited `IWavePosition` helper is removed so it cannot bypass the
dedicated observer with NAudio's Boolean clock wrapper. No clock query is added
to the render callback.

The observation adapter also caches the existing padding reads and successful
ReleaseBuffer frame counts/timestamps for diagnosis. This adds no COM call,
render-thread allocation, lock, or scheduling change. Bounded versioned reads
attach query-before/after facts to raw clock records; an incoherent read remains
unknown. These facts do not relax clock qualification or prove physical audio
quality. The first second after Start is excluded from steady zero-padding
counts.

The production default continues using the original NAudio `WasapiOut`.
`ClockedWasapiOut` is selected only by the explicit observation option. Its
`IAudioClock` observer preserves raw HRESULT values and rejects `S_FALSE`, rather
than relying on NAudio 2.2.1 `AudioClockClient.GetPosition`'s Boolean wrapper.

`OutputClockNative.cs` declares only the Windows COM ABI needed to activate the
render client and read `IAudioClock`. Its raw position is interpreted with the
reported frequency; its correlated QPC value has 100 ns units, distinct from
`Stopwatch.GetTimestamp()` ticks.

Native service acquisition, reads and release run on the observer MTA thread.
The observer stops before render-client disposal. If a faulty native read does
not finish within two seconds, observation is disabled and client release is
deferred until that call returns. The explicit Stop/Dispose caller still requests
render stop, then receives a TimeoutException and WindowsAudio.Failure is set;
the Engine cannot classify this as a clean exit. If render stop also throws,
both failures are preserved in an AggregateException. The two-second budget is
for the observer join; it cannot guarantee that a faulty driver's render Stop
itself returns. Normal clock-query failure only disables observation and does
not stop healthy audio. No blocked native thread is aborted or freed underneath
its pending service call.

The observation renderer also caches its existing padding query and successful
ReleaseBuffer frame counts with raw QPC stamps. No COM call is added for these
facts. A single-writer versioned cache gives readers at most three attempts,
otherwise Unknown; the raw clock sample retains before/after cache snapshots.
Counters use output frames and raw QPC ticks, distinct from IAudioClock position
units. Steady zero-padding counts exclude the first second after Start. A zero
padding-query timestamp means no real padding observation has been published.
These facts diagnose scheduling/endpoint behavior and do not relax clock-fit
qualification or prove continuous physical playback by themselves.
