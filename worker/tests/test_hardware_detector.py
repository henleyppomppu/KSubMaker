"""Hardware detection must always produce a usable payload and never raise.

The rule these tests encode, after the ``cublas64_12.dll`` bug report: ``cudaAvailable`` is true only
when a device exists **and** the CUDA support libraries load. Reporting the device half alone is
what let the app recommend a GPU model on a machine where every model load was going to fail.
"""

from __future__ import annotations

import subprocess
import sys
import types
from typing import Any, Iterator

import pytest

from ksubmaker_worker import cuda_setup, hardware_detector
from ksubmaker_worker.hardware_detector import detect, largest_free_vram_bytes

REQUIRED_KEYS = {
    "gpus",
    "cudaAvailable",
    "cudaDeviceDetected",
    "cudaLibrariesAvailable",
    "missingCudaLibraries",
    "cudaVersion",
    "cpuName",
    "logicalCores",
    "totalRamBytes",
    "availableRamBytes",
    "warnings",
}


@pytest.fixture(autouse=True)
def _clear_cuda_setup_cache() -> Iterator[None]:
    """detect() calls ensure_registered(); leaving its cache set would leak between tests."""
    cuda_setup.reset()
    yield
    cuda_setup.reset()


def _pretend_gpu_present(monkeypatch: pytest.MonkeyPatch, *, devices: int = 1) -> None:
    """Install a fake ``ctranslate2`` so the device half of the probe answers yes."""
    fake = types.ModuleType("ctranslate2")
    fake.get_cuda_device_count = lambda: devices  # type: ignore[attr-defined]
    monkeypatch.setitem(sys.modules, "ctranslate2", fake)

    monkeypatch.setattr(hardware_detector.shutil, "which", lambda _name: "/usr/bin/nvidia-smi")
    monkeypatch.setattr(
        hardware_detector.subprocess,
        "run",
        lambda argv, **_k: subprocess.CompletedProcess(
            argv, 0, b"0, NVIDIA GeForce RTX 3080 Ti, 12288, 11000, 581.15, 8.6\n", b""
        ),
    )


def test_detect_returns_the_hardware_event_shape() -> None:
    payload = detect()

    assert REQUIRED_KEYS <= set(payload)
    assert isinstance(payload["gpus"], list)
    assert isinstance(payload["cudaAvailable"], bool)
    assert isinstance(payload["warnings"], list)


def test_detect_reports_real_cpu_and_memory_on_this_host() -> None:
    payload = detect()

    assert payload["logicalCores"] >= 1
    assert payload["totalRamBytes"] > 0
    assert payload["availableRamBytes"] > 0
    assert payload["availableRamBytes"] <= payload["totalRamBytes"]


def test_no_gpu_produces_a_warning_not_a_failure(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(hardware_detector.shutil, "which", lambda _name: None)

    payload = detect()

    assert payload["gpus"] == []
    assert payload["cudaAvailable"] is False
    assert any("GPU" in warning for warning in payload["warnings"])


def test_nvidia_smi_output_is_parsed(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(hardware_detector.shutil, "which", lambda _name: "/usr/bin/nvidia-smi")

    def fake_run(argv, **_kwargs: Any):  # noqa: ANN001, ANN202
        if "--query-gpu" in " ".join(argv):
            stdout = b"0, NVIDIA GeForce RTX 4070, 12282, 11500, 550.54.14, 8.9\n"
        else:
            stdout = b"| NVIDIA-SMI 550.54.14   Driver Version: 550.54.14   CUDA Version: 12.4  |\n"
        return subprocess.CompletedProcess(argv, 0, stdout, b"")

    monkeypatch.setattr(hardware_detector.subprocess, "run", fake_run)

    payload = detect()
    gpu = payload["gpus"][0]

    assert gpu["index"] == 0
    assert gpu["name"] == "NVIDIA GeForce RTX 4070"
    assert gpu["totalVramBytes"] == 12282 * 1024 * 1024
    assert gpu["freeVramBytes"] == 11500 * 1024 * 1024
    assert gpu["driverVersion"] == "550.54.14"
    assert gpu["computeCapability"] == "8.9"


def test_a_gpu_without_a_working_cuda_runtime_is_called_out(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(hardware_detector.shutil, "which", lambda _name: "/usr/bin/nvidia-smi")
    monkeypatch.setattr(
        hardware_detector.subprocess,
        "run",
        lambda argv, **_k: subprocess.CompletedProcess(argv, 0, b"0, Fake GPU, 8192, 8000, 1.0, 7.5\n", b""),
    )

    payload = detect()

    assert payload["gpus"]
    assert payload["cudaAvailable"] is False
    assert any("CUDA" in warning for warning in payload["warnings"])


# ---------------------------------------------------------------------------
# cudaAvailable = device AND support libraries
# ---------------------------------------------------------------------------


def test_a_device_plus_working_libraries_reports_cuda_available(monkeypatch: pytest.MonkeyPatch) -> None:
    _pretend_gpu_present(monkeypatch)
    monkeypatch.setattr(cuda_setup, "probe_support_libraries", lambda: (True, [], []))

    payload = detect()

    assert payload["cudaDeviceDetected"] is True
    assert payload["cudaLibrariesAvailable"] is True
    assert payload["cudaAvailable"] is True
    assert payload["missingCudaLibraries"] == []


def test_a_device_without_cublas_is_reported_as_cuda_unavailable(monkeypatch: pytest.MonkeyPatch) -> None:
    """The exact bug: RTX 3080 Ti, healthy driver, no cuBLAS. It used to report CUDA=true."""
    _pretend_gpu_present(monkeypatch)
    monkeypatch.setattr(
        cuda_setup,
        "probe_support_libraries",
        lambda: (False, ["cublas64_12.dll"], ["cublas64_12.dll: OSError(126)"]),
    )

    payload = detect()

    assert payload["cudaDeviceDetected"] is True, "the driver really is fine; say so"
    assert payload["cudaLibrariesAvailable"] is False
    assert payload["cudaAvailable"] is False, "a GPU model must not be recommended on this machine"
    assert payload["missingCudaLibraries"] == ["cublas64_12.dll"]


def test_the_missing_library_warning_names_the_file_and_the_fix(monkeypatch: pytest.MonkeyPatch) -> None:
    _pretend_gpu_present(monkeypatch)
    monkeypatch.setattr(
        cuda_setup, "probe_support_libraries", lambda: (False, ["cublas64_12.dll"], [])
    )

    warnings = detect()["warnings"]
    warning = next(w for w in warnings if "cublas64_12.dll" in w)

    assert "build-worker.ps1" in warning
    assert "드라이버" in warning, "the driver is fine — the warning must not send the user there"
    # The generic "update your driver" warning must not also fire; it is the wrong advice here.
    assert not any("그래픽 드라이버를 업데이트" in w for w in warnings)


def test_no_device_means_the_libraries_are_never_probed(monkeypatch: pytest.MonkeyPatch) -> None:
    """A CPU-only machine legitimately has no cuBLAS; warning about it would be noise."""
    monkeypatch.setattr(hardware_detector.shutil, "which", lambda _name: None)

    def explode() -> tuple[bool, list[str], list[str]]:  # pragma: no cover - must not be called
        raise AssertionError("the support-library probe must not run without a device")

    monkeypatch.setattr(cuda_setup, "probe_support_libraries", explode)

    payload = detect()

    assert payload["cudaDeviceDetected"] is False
    assert payload["cudaLibrariesAvailable"] is True
    assert payload["cudaAvailable"] is False
    assert payload["missingCudaLibraries"] == []


def test_detect_registers_the_dll_directories_before_probing(monkeypatch: pytest.MonkeyPatch) -> None:
    """Probing before registration would report a correctly installed cuBLAS as missing."""
    order: list[str] = []

    monkeypatch.setattr(cuda_setup, "ensure_registered", lambda: order.append("register"))
    monkeypatch.setattr(
        cuda_setup,
        "probe_support_libraries",
        lambda: (order.append("probe"), (True, [], []))[1],
    )
    _pretend_gpu_present(monkeypatch)

    detect()

    assert order == ["register", "probe"]


def test_a_broken_nvidia_smi_does_not_raise(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(hardware_detector.shutil, "which", lambda _name: "/usr/bin/nvidia-smi")

    def explode(*_args: Any, **_kwargs: Any):  # noqa: ANN202
        raise OSError("nvidia-smi is a broken symlink")

    monkeypatch.setattr(hardware_detector.subprocess, "run", explode)

    payload = detect()

    assert payload["gpus"] == []
    assert payload["warnings"]


def test_unparseable_nvidia_smi_rows_are_skipped(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(hardware_detector.shutil, "which", lambda _name: "/usr/bin/nvidia-smi")
    monkeypatch.setattr(
        hardware_detector.subprocess,
        "run",
        lambda argv, **_k: subprocess.CompletedProcess(
            argv, 0, b"garbage\n0, Real GPU, 8192, 8000, 1.0, 7.5\n\n", b""
        ),
    )

    payload = detect()
    assert [gpu["name"] for gpu in payload["gpus"]] == ["Real GPU"]


def test_a_nonzero_nvidia_smi_exit_is_handled(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(hardware_detector.shutil, "which", lambda _name: "/usr/bin/nvidia-smi")
    monkeypatch.setattr(
        hardware_detector.subprocess,
        "run",
        lambda argv, **_k: subprocess.CompletedProcess(argv, 9, b"", b"driver mismatch"),
    )

    payload = detect()
    assert payload["gpus"] == []
    assert payload["warnings"]


def test_unreadable_memory_information_degrades_to_zero(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(hardware_detector, "_linux_meminfo", lambda: (0, 0))

    def no_sysconf(_name: str) -> int:
        raise OSError("no sysconf here")

    monkeypatch.setattr(hardware_detector.os, "sysconf", no_sysconf)

    payload = detect()

    assert payload["totalRamBytes"] == 0
    assert any("메모리" in warning for warning in payload["warnings"])


def test_largest_free_vram_of_no_gpus_is_zero() -> None:
    assert largest_free_vram_bytes({"gpus": []}) == 0


def test_largest_free_vram_picks_the_roomiest_card() -> None:
    profile = {"gpus": [{"freeVramBytes": 2000}, {"freeVramBytes": 9000}, {"freeVramBytes": 500}]}
    assert largest_free_vram_bytes(profile) == 9000


def test_largest_free_vram_tolerates_junk_entries() -> None:
    profile = {"gpus": ["not a dict", {"freeVramBytes": "1234"}, {}]}
    assert largest_free_vram_bytes(profile) == 1234


# ---------------------------------------------------------------------------
# float16 을 쓸 수 없는 세대 — VRAM 은 충분한데 연산이 안 되는 경우
# ---------------------------------------------------------------------------


def _profile(name: str, capability: str | None) -> dict:
    return {"gpus": [{"name": name, "computeCapability": capability}]}


@pytest.mark.parametrize("capability", ["6.1", "6.0", "5.2"])
def test_pascal_and_older_cannot_use_float16(capability: str) -> None:
    """실제 신고: GTX 1060 이 10% 에서 모델을 못 불러왔고 int8 로 바꾸니 바로 됐다.

    GP104/GP106 은 fp16 을 fp32 의 1/64 속도로 돌린다. VRAM 만 보고 결정하면 안 되는 이유다.
    """
    assert hardware_detector.lacks_usable_float16(_profile("NVIDIA GeForce GTX 1060 6GB", capability))


@pytest.mark.parametrize("capability", ["7.0", "7.5", "8.6", "8.9", "12.0"])
def test_volta_and_newer_keep_float16(capability: str) -> None:
    assert not hardware_detector.lacks_usable_float16(_profile("NVIDIA GeForce RTX 3080 Ti", capability))


@pytest.mark.parametrize(
    ("name", "pascal"),
    [
        ("NVIDIA GeForce GTX 1080 Ti", True),
        ("NVIDIA GeForce GTX 1050 Ti", True),
        ("NVIDIA TITAN Xp", True),
        # Turing 인데 이름이 "GTX 1" 로 시작한다 — 이름 판정의 유일한 함정.
        ("NVIDIA GeForce GTX 1660 SUPER", False),
        ("NVIDIA GeForce RTX 4090", False),
    ],
)
def test_the_name_decides_when_the_driver_reports_no_capability(name: str, pascal: bool) -> None:
    assert hardware_detector.lacks_usable_float16(_profile(name, None)) is pascal


def test_an_unknown_or_absent_gpu_is_assumed_capable() -> None:
    # 모르는 카드에 int8 을 강요하면 최신 GPU 가 전부 느려진다. 반대 방향의 손해가 더 크다.
    assert not hardware_detector.lacks_usable_float16(_profile("NVIDIA Something New", None))
    assert not hardware_detector.lacks_usable_float16({"gpus": []})
    assert not hardware_detector.lacks_usable_float16({})


def test_the_worker_default_follows_the_same_rule() -> None:
    """호스트가 computeType=null 을 보내는 '자동' 경로가 실제로 여기로 온다."""
    from ksubmaker_worker import transcriber

    assert transcriber._default_compute_type("cpu") == "int8"

