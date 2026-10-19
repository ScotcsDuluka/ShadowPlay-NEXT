# D3D10 Frame Publisher â€” reserved

This folder is reserved for the API-oriented OSC publisher layout. The current OSC frame bridge only declares consumer APIs for D3D9 (9) and D3D11 (11); it does not currently declare a D3D10 consumer API.

Before adding D3D10, define the consumer protocol, resource-sharing/readback path, and Hook-side API selection. The existence of this folder does not mean D3D10 works.