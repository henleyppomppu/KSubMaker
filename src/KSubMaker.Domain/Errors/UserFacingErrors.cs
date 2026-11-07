namespace KSubMaker.Domain.Errors;

/// <summary>
/// Maps an <see cref="ErrorCodes"/> value onto a short Korean sentence a non-technical user can act on.
/// Stack traces never reach the UI; they go to the log file that the "로그 보기" button opens.
/// </summary>
public static class UserFacingErrors
{
    public static string Describe(string? code, string? detail = null)
    {
        var text = code switch
        {
            ErrorCodes.VideoNotFound => "영상 파일을 찾을 수 없습니다. 파일이 이동되었거나 삭제되었는지 확인하세요.",
            ErrorCodes.VideoUnreadable => "영상 파일을 읽을 수 없습니다. 파일이 손상되었을 수 있습니다.",
            ErrorCodes.AudioTrackNotFound => "영상에 오디오 트랙이 없어 자막을 만들 수 없습니다.",
            ErrorCodes.FfmpegNotFound => "FFmpeg 실행 파일을 찾을 수 없습니다. 설치가 손상되었을 수 있습니다.",
            ErrorCodes.FfmpegFailed => "음성 추출에 실패했습니다. 영상 파일 형식을 확인하세요.",
            ErrorCodes.CudaNotAvailable => "CUDA를 사용할 수 없어 CPU 모드로 동작합니다. 처리 속도가 크게 느려집니다.",
            ErrorCodes.CudaLibraryMissing =>
                "CUDA 지원 라이브러리(cuBLAS 12 / cuDNN 9)를 불러오지 못했습니다. " +
                "그래픽 드라이버만으로는 부족합니다. scripts\\build-worker.ps1로 워커를 다시 설치하거나 " +
                "설정에서 CPU 모드로 전환하세요.",
            ErrorCodes.CudaOutOfMemory => "GPU 메모리가 부족합니다. 더 작은 모델을 선택하거나 다른 GPU 작업을 종료하세요.",
            ErrorCodes.WhisperModelNotFound => "음성 인식 모델이 설치되어 있지 않습니다. 모델 관리 화면에서 다운로드하세요.",
            ErrorCodes.WhisperModelLoadFailed => "음성 인식 모델을 불러오지 못했습니다. 모델 파일을 검증하거나 다시 다운로드하세요.",
            ErrorCodes.TranscriptionFailed => "음성 인식에 실패했습니다.",
            ErrorCodes.NoSpeech => "음성이 없습니다. 자막으로 만들 내용이 없습니다.",
            ErrorCodes.TranslationModelNotFound => "번역 모델이 설치되어 있지 않습니다. 모델 관리 화면에서 다운로드하세요.",
            ErrorCodes.TranslationFailed => "번역에 실패했습니다.",
            ErrorCodes.InvalidTranslationResponse => "번역 결과 형식이 올바르지 않아 해당 구간을 다시 시도했습니다.",
            ErrorCodes.OutputWriteFailed => "자막 파일을 저장하지 못했습니다. 폴더 쓰기 권한과 디스크 여유 공간을 확인하세요.",
            ErrorCodes.DiskSpaceLow => "디스크 여유 공간이 부족합니다.",
            ErrorCodes.WorkerCrashed => "AI 작업 프로세스가 예기치 않게 종료되었습니다.",
            ErrorCodes.OperationCancelled => "작업이 취소되었습니다.",
            ErrorCodes.ModelDownloadFailed => "모델 다운로드에 실패했습니다. 네트워크 상태를 확인하세요.",
            ErrorCodes.ModelVerificationFailed => "모델 파일 검증에 실패했습니다. 파일이 손상되었을 수 있으니 다시 다운로드하세요.",
            ErrorCodes.ProtocolError => "작업 프로세스와의 통신에 문제가 발생했습니다.",
            _ => "알 수 없는 오류가 발생했습니다."
        };

        return string.IsNullOrWhiteSpace(detail) ? text : $"{text} ({detail})";
    }
}
