-- P3-14 v4 다국어 — 클라이언트 생성 일반 오류 문구(권한/서버/연결/폴백) EnUs 리소스 시드.
-- 규약: 한국어가 기본(코드 인라인 폴백)이라 비-한국어만 시드한다. MENU_ID는 공통 문구 규약대로 'COMMON'.
-- {0}=HTTP 상태 코드 자리표시자를 유지한다(클라이언트 string.Format 조립).
-- 서버 모듈 Error.Description(자유 문장)의 다국어는 오류코드→리소스+언어 전파 아크로 별도(본 시드 범위 밖).
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE) VALUES
    ('error.forbidden',     'COMMON', 'EnUs', 'You do not have permission for this action. Please request access from an administrator.'),
    ('error.server',        'COMMON', 'EnUs', 'A server error occurred (HTTP {0}). Please try again later.'),
    ('error.unreachable',   'COMMON', 'EnUs', 'Cannot reach the server. Please try again later.'),
    ('error.requestFailed', 'COMMON', 'EnUs', 'The request failed (HTTP {0}).'),
    ('error.uploadFailed',  'COMMON', 'EnUs', 'Upload failed (HTTP {0}).');
