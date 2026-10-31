# Project structure and origin

The root is the complete working project. No build or runtime file is loaded
from `TEMPO_FOLDER_ datv-rx`.

| Path | Purpose |
| --- | --- |
| `core/` | Existing C++ receiver, DSP, transport-stream code and public headers |
| `rust/` | Existing local Rust workspace and C ABI integration |
| `tools/` | `datvrx` receiver and `dvbstool` generator/benchmark |
| `CMakeLists.txt` | Shared Windows and Linux build definition |
| `scripts/windows/` | Windows build entry point |
| `scripts/linux/` | Native Linux/Pi build entry point |
| `scripts/selftest.sh` | Synthetic end-to-end video reception test |
| `docs/` | Platform setup, structure and validation notes |
| `build/windows/`, `build/linux/` | Separate generated outputs, ignored by Git |
| `LICENSE` | Original GPL licence |

The original source package was copied in full from
`TEMPO_FOLDER_ datv-rx/datv-rx`, including all source, Cargo.lock, licence and
documentation. Shared DVB-T/T2 and transport-stream files remain because the
existing CMake build includes them. Receiver algorithms have not been rewritten.
The Windows no-GPU stub now defines its declared GPU-memory query with a zero
return value; this fixes the missing symbol found during Windows linking.
Acquisition's bandwidth check also honors an explicit roll-off hint, enabling
the verified 1.5 MSym/s beacon trial at 2.56 MSps. Automatic defaults are unchanged.

The original README is retained as `docs/UPSTREAM_README.md` for provenance and
its receiver option details. Its historical test claims belong to the supplied
package. See `docs/VALIDATION.md` for checks performed in this workspace.

The original temporary folder is retained only for comparison and can be removed
once the independent working copy has been reviewed. It is ignored by Git.
