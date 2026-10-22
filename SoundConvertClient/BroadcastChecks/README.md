# UDP broadcast checks

Run `dotnet run --project SoundConvertClient/BroadcastChecks/BroadcastChecks.csproj -c Release`.

Checks compile the production UDP codec and exercise real selected-NIC UDP broadcast sockets with an isolated software board identity: cancellation, reply-port TLV, no-address metadata, cross-subnet candidate metadata, exact board identity, boot/revision/transaction validation, and lost APPLY acknowledgment with the same request ID.

The fixture binds UDP 49160 and sends requests only for its own synthetic device ID. It makes no physical-board, source-IP 0.0.0.0, W5500 or cross-VLAN acceptance claim. Hardware configuration scenarios use `tools/Test-UdpBroadcastNetwork.ps1` only when explicitly authorized.
