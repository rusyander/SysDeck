# mpo-fix

Disables Multi-Plane Overlay (`HKLM\...\Dwm\OverlayTestMode=5`). Cures cursor
and window freezes when the pointer crosses between monitors on mixed-GPU /
mixed-refresh topologies (for example two 165 Hz monitors on a discrete GPU + a 60 Hz one on
the iGPU). Windows and driver updates can silently remove the value and bring the
stalls back - re-apply after driver/Windows updates if crossing-lag returns.

- `apply.ps1` — set the value (elevated); `apply.ps1 -Revert` — remove it.
- Verify: `reg query "HKLM\SOFTWARE\Microsoft\Windows\Dwm" /v OverlayTestMode` → 5.
- Hardware-level alternative that removes the root cause: plug every monitor
  into the same GPU.
