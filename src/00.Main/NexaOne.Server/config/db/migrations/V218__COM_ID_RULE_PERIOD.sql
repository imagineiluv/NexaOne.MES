-- COM_ID_RULE — 리셋 주기 판정을 위한 마지막 발급 기간.
-- RESET_CYCLE(Daily/Monthly/Yearly) 경계가 바뀌면 시퀀스를 1로 되돌리는지 판단하기 위해
-- 직전 발급의 기간 키(yyyyMMdd/yyyyMM/yyyy, Never는 공백)를 보존한다.
ALTER TABLE COM_ID_RULE
    ADD SEQ_PERIOD NVARCHAR(8) NULL;
