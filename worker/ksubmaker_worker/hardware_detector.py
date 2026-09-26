"""Hardware detection for the ``hardware`` event.

:func:`detect` must never raise. It is called at startup and from the settings screen; a machine
with no nvidia-smi, no ctranslate2 and an unreadable /proc must still produce a usable payload
(all zeroes plus a warning) rather than fail the handshake.

**``cudaAvailable`` means "the next model load will work", not "a driver is installed."** It used
to be ``ctranslate2.get_cuda_device_count() > 0``, which only needs the *driver*: on a machine
without the CUDA 12 cuBLAS / cuDNN 9 runtime the app then recommended a GPU model, showed
"CUDA 사용 가능" in the status bar, and the user discovered the truth an hour later when a job died
with ``Library cublas64_12.dll is not found or cannot be loaded``. A detector that reports a
capability the very next step cannot use is worse than no detector, so the device check is now
*and*-ed with an actual load of the support libraries (:mod:`ksubmaker_worker.cuda_setup`).
"""

from __future__ import annotations

import os
import platform
import shutil
import subprocess
import sys
from dataclasses import dataclass
from typing import Any, Final

from . import cuda_setup
from .logging_setup import get_logger

_log = get_logger("hardware")

_NVIDIA_SMI_TIMEOUT = 10
_NVIDIA_QUERY = "index,name,memory.total,memory.free,driver_version,compute_cap"


def detect() -> dict[str, Any]:
    """Build the ``HardwareEvent`` payload. Never raises."""
    warnings: list[str] = []

    gpus = _detect_gpus(warnings)
    cuda = _detect_cuda(warnings)

    if gpus and cuda.device_detected and not cuda.libraries_available:
        # The specific case that produced the bug report: a healthy driver and a visible device,
        # but no cuBLAS/cuDNN, so every model load fails. Name the file and the fix.
        warnings.append(
            "NVIDIA GPU와 드라이버는 정상이지만 CUDA 지원 라이브러리("
            + ", ".join(cuda.missing_libraries or list(cuda_setup.REQUIRED_DLLS))
            + ")를 찾지 못했습니다. CUDA 12용 cuBLAS와 cuDNN 9가 필요합니다. "
            "scripts\\build-worker.ps1을 다시 실행해 워커를 설치하세요. 지금은 CPU 모드로 동작합니다."
        )
    elif gpus and not cuda.available:
        warnings.append(
            "NVIDIA GPU는 감지되었지만 CUDA 런타임을 사용할 수 없습니다. "
            "그래픽 드라이버를 업데이트하거나 CPU 모드로 실행하세요."
        )
    if not gpus:
        warnings.append("NVIDIA GPU를 찾지 못했습니다. CPU로 실행하면 처리 속도가 매우 느립니다.")

    total_ram, available_ram = _detect_memory(warnings)
    logical_cores = os.cpu_count() or 0

    if total_ram and total_ram < 8 * 1024**3:
        warnings.append("시스템 메모리가 8GB 미만입니다. 큰 모델은 실행하지 못할 수 있습니다.")

    return {
        "gpus": gpus,
        # Protocol 1.2: the three CUDA fields below are reported together. `cudaAvailable` is the
        # conjunction and stays the single field a 1.1 host needs to read.
        "cudaAvailable": cuda.available,
        "cudaDeviceDetected": cuda.device_detected,
        "cudaLibrariesAvailable": cuda.libraries_available,
        "missingCudaLibraries": cuda.missing_libraries,
        "cudaVersion": cuda.version,
        "cpuName": _detect_cpu_name(),
        "logicalCores": logical_cores,
        "totalRamBytes": total_ram,
        "availableRamBytes": available_ram,
        "warnings": warnings,
    }


# ---------------------------------------------------------------------------
# GPU
# ---------------------------------------------------------------------------


def _detect_gpus(warnings: list[str]) -> list[dict[str, Any]]:
    smi = shutil.which("nvidia-smi")
    if smi is None:
        _log.info("nvidia-smi not found; assuming no NVIDIA GPU")
        return []

    argv = [smi, f"--query-gpu={_NVIDIA_QUERY}", "--format=csv,noheader,nounits"]

    try:
        completed = subprocess.run(  # noqa: S603 - list argv, shell=False
            argv,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
            timeout=_NVIDIA_SMI_TIMEOUT,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        _log.warning("nvidia-smi failed: %r", exc)
        warnings.append("GPU 정보를 조회하지 못했습니다. 그래픽 드라이버를 확인하세요.")
        return []

    if completed.returncode != 0:
        detail = completed.stderr.decode("utf-8", "replace").strip()
        _log.warning("nvidia-smi exited with %s: %s", completed.returncode, detail)
        warnings.append("GPU 정보를 조회하지 못했습니다. 그래픽 드라이버를 확인하세요.")
        return []

    gpus: list[dict[str, Any]] = []
    for line in completed.stdout.decode("utf-8", "replace").splitlines():
        line = line.strip()
        if not line:
            continue

        parts = [part.strip() for part in line.split(",")]
        if len(parts) < 4:
            _log.warning("unparseable nvidia-smi row: %r", line)
            continue

        gpus.append(
            {
                "index": _to_int(parts[0], default=len(gpus)),
                "name": parts[1],
                # nounits means MiB.
                "totalVramBytes": _to_int(parts[2]) * 1024 * 1024,
                "freeVramBytes": _to_int(parts[3]) * 1024 * 1024,
                "driverVersion": parts[4] if len(parts) > 4 and parts[4] else None,
                "computeCapability": parts[5] if len(parts) > 5 and parts[5] else None,
            }
        )

    return gpus


@dataclass(frozen=True)
class CudaStatus:
    """The CUDA half of the ``hardware`` payload, split into the two things that can differ."""

    #: A CUDA device can be opened — this only proves the *driver* works.
    device_detected: bool

    #: cuBLAS 12 and cuDNN 9 actually loaded. Always True on non-Windows (see cuda_setup).
    libraries_available: bool

    #: Support DLLs that failed to load, in the order they were probed.
    missing_libraries: list[str]

    version: str | None

    @property
    def available(self) -> bool:
        """What the host reads as ``cudaAvailable``: a device **and** the libraries it needs."""
        return self.device_detected and self.libraries_available


def _detect_cuda(warnings: list[str]) -> CudaStatus:
    """Two questions, deliberately asked separately.

    1. *Is there a device?* ``ctranslate2.get_cuda_device_count()`` answers it, and needs only the
       driver. nvidia-smi reporting a GPU proves even less.
    2. *Will a model load?* That needs cuBLAS (CUDA 12) and cuDNN 9, which are toolkit libraries
       the driver does not ship and the ctranslate2 wheel does not bundle.

    Reporting only (1) is what produced the ``cublas64_12.dll is not found`` bug report, so the
    payload now carries both and ``cudaAvailable`` is their conjunction.
    """
    device_detected = False
    version: str | None = None

    # The DLL search paths have to be in place before the probe, otherwise a correctly installed
    # nvidia-cublas-cu12 would still fail to load and we would report a false negative.
    cuda_setup.ensure_registered()

    try:
        import ctranslate2  # noqa: PLC0415 - deliberately lazy

        device_detected = ctranslate2.get_cuda_device_count() > 0
    except ImportError:
        warnings.append("CTranslate2가 설치되어 있지 않아 GPU 사용 가능 여부를 확인할 수 없습니다.")
    except Exception as exc:  # noqa: BLE001 - a broken CUDA install throws all sorts
        _log.warning("ctranslate2 CUDA probe failed: %r", exc)
        warnings.append("CUDA 초기화에 실패했습니다. CPU 모드로 동작합니다.")

    try:
        import torch  # noqa: PLC0415

        version = getattr(torch.version, "cuda", None)
        if not device_detected:
            device_detected = bool(torch.cuda.is_available())
    except ImportError:
        pass
    except Exception as exc:  # noqa: BLE001
        _log.debug("torch CUDA probe failed: %r", exc)

    if version is None:
        version = _cuda_version_from_smi()

    libraries_available = True
    missing: list[str] = []

    if device_detected:
        # Only worth probing when there is something to accelerate; on a CPU-only machine the
        # libraries are legitimately absent and a warning about them would be noise.
        libraries_available, missing, errors = cuda_setup.probe_support_libraries()
        if errors:
            _log.warning("CUDA support library probe: %s", "; ".join(errors))

    return CudaStatus(
        device_detected=device_detected,
        libraries_available=libraries_available,
        missing_libraries=missing,
        version=version,
    )


def _cuda_version_from_smi() -> str | None:
    smi = shutil.which("nvidia-smi")
    if smi is None:
        return None

    try:
        completed = subprocess.run(  # noqa: S603
            [smi],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            check=False,
            timeout=_NVIDIA_SMI_TIMEOUT,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        _log.debug("nvidia-smi banner read failed: %r", exc)
        return None

    for line in completed.stdout.decode("utf-8", "replace").splitlines():
        if "CUDA Version" in line:
            _, _, tail = line.partition("CUDA Version:")
            token = tail.strip().split()[0] if tail.strip() else ""
            return token.strip("| ") or None

    return None


# ---------------------------------------------------------------------------
# CPU / RAM
# ---------------------------------------------------------------------------


def _detect_cpu_name() -> str | None:
    if sys.platform.startswith("linux"):
        name = _linux_cpu_name()
        if name:
            return name

    if sys.platform.startswith("win"):
        name = os.environ.get("PROCESSOR_IDENTIFIER")
        if name:
            return name.strip()

    if sys.platform == "darwin":
        try:
            completed = subprocess.run(  # noqa: S603
                ["/usr/sbin/sysctl", "-n", "machdep.cpu.brand_string"],
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
                check=False,
                timeout=5,
            )
            name = completed.stdout.decode("utf-8", "replace").strip()
            if name:
                return name
        except (OSError, subprocess.SubprocessError) as exc:
            _log.debug("sysctl cpu probe failed: %r", exc)

    return platform.processor() or platform.machine() or None


def _linux_cpu_name() -> str | None:
    try:
        with open("/proc/cpuinfo", "r", encoding="utf-8", errors="replace") as handle:
            for line in handle:
                if line.lower().startswith("model name"):
                    _, _, value = line.partition(":")
                    return value.strip() or None
    except OSError as exc:
        _log.debug("/proc/cpuinfo unreadable: %r", exc)
    return None


def _detect_memory(warnings: list[str]) -> tuple[int, int]:
    if sys.platform.startswith("win"):
        return _windows_memory(warnings)

    if sys.platform.startswith("linux"):
        total, available = _linux_meminfo()
        if total:
            return total, available

    # Portable POSIX fallback (macOS, BSD, or a Linux without /proc).
    try:
        page_size = os.sysconf("SC_PAGE_SIZE")
        total_pages = os.sysconf("SC_PHYS_PAGES")
        total = int(page_size) * int(total_pages)
        try:
            available = int(page_size) * int(os.sysconf("SC_AVPHYS_PAGES"))
        except (OSError, ValueError):
            available = total
        return total, available
    except (OSError, ValueError, AttributeError) as exc:
        _log.debug("sysconf memory probe failed: %r", exc)

    warnings.append("시스템 메모리 정보를 확인하지 못했습니다.")
    return 0, 0


def _linux_meminfo() -> tuple[int, int]:
    total = 0
    available = 0

    try:
        with open("/proc/meminfo", "r", encoding="utf-8", errors="replace") as handle:
            for line in handle:
                key, _, value = line.partition(":")
                tokens = value.split()
                if not tokens:
                    continue
                kib = _to_int(tokens[0])
                if key == "MemTotal":
                    total = kib * 1024
                elif key == "MemAvailable":
                    available = kib * 1024
    except OSError as exc:
        _log.debug("/proc/meminfo unreadable: %r", exc)
        return 0, 0

    return total, available or total


def _windows_memory(warnings: list[str]) -> tuple[int, int]:
    """GlobalMemoryStatusEx via ctypes; no third-party dependency for one struct."""
    try:
        import ctypes  # noqa: PLC0415

        class MemoryStatusEx(ctypes.Structure):
            _fields_ = [
                ("dwLength", ctypes.c_ulong),
                ("dwMemoryLoad", ctypes.c_ulong),
                ("ullTotalPhys", ctypes.c_ulonglong),
                ("ullAvailPhys", ctypes.c_ulonglong),
                ("ullTotalPageFile", ctypes.c_ulonglong),
                ("ullAvailPageFile", ctypes.c_ulonglong),
                ("ullTotalVirtual", ctypes.c_ulonglong),
                ("ullAvailVirtual", ctypes.c_ulonglong),
                ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
            ]

        status = MemoryStatusEx()
        status.dwLength = ctypes.sizeof(MemoryStatusEx)

        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):  # type: ignore[attr-defined]
            return int(status.ullTotalPhys), int(status.ullAvailPhys)

        warnings.append("시스템 메모리 정보를 확인하지 못했습니다.")
        return 0, 0
    except (ImportError, AttributeError, OSError, ValueError) as exc:
        _log.warning("GlobalMemoryStatusEx failed: %r", exc)
        warnings.append("시스템 메모리 정보를 확인하지 못했습니다.")
        return 0, 0


def _to_int(value: Any, default: int = 0) -> int:
    try:
        return int(str(value).strip())
    except (TypeError, ValueError):
        return default


def largest_free_vram_bytes(profile: dict[str, Any] | None = None) -> int:
    """Free VRAM of the roomiest GPU, used to size ``--n-gpu-layers``. 0 when there is none."""
    data = profile if profile is not None else detect()
    gpus = data.get("gpus") or []
    best = 0
    for gpu in gpus:
        if isinstance(gpu, dict):
            best = max(best, _to_int(gpu.get("freeVramBytes")))
    return best


#: GTX 10 시리즈와 그 세대의 TITAN/Quadro. 드라이버가 ``compute_cap`` 을 답하지 않을 때만 쓴다.
#: "GTX 1660"/"GTX 1650" 은 Turing(7.5) 이라 여기 걸리면 안 되므로 앞자리가 아니라 시리즈
#: 숫자를 통째로 본다.
_PASCAL_NAME_MARKERS: Final = ("1030", "1050", "1060", "1070", "1080", "TITAN X", "TITAN XP")


def lacks_usable_float16(profile: dict[str, Any] | None = None) -> bool:
    """Whether the primary GPU would run a float16 model slower than it runs float32.

    Pascal (compute capability 6.x) is the case this exists for: GP104/GP106 execute fp16
    arithmetic at **1/64** of their fp32 rate, so a card with ample VRAM for a float16 model still
    cannot execute one at a usable speed. A GTX 1060 was reported failing at 10% of a run and
    working immediately once the compute type became int8.

    Mirrors ``HardwareRecommendationPolicy.LacksUsableFloat16`` on the C# side. The host decides
    what to *recommend*; this decides what an unspecified ``computeType`` actually becomes, which
    is the path an "auto" user takes — the host sends null and never gets a say.

    An unknown card reads as capable: recommending int8 needlessly costs a little accuracy, while
    refusing float16 on hardware that has it would slow down every modern GPU.
    """
    data = profile if profile is not None else detect()
    gpus = data.get("gpus") or []
    if not gpus or not isinstance(gpus[0], dict):
        return False

    gpu = gpus[0]

    capability = str(gpu.get("computeCapability") or "").strip()
    if capability:
        head = capability.split(".", 1)[0]
        try:
            # Maxwell(5.x) 는 fp16 경로 자체가 없고, Pascal(6.x) 은 있는데 fp32 보다 느리다.
            # Volta(7.0) 부터 텐서 코어가 붙는다.
            return int(head) <= 6
        except ValueError:
            pass

    name = str(gpu.get("name") or "").upper()
    return any(marker in name for marker in _PASCAL_NAME_MARKERS)

