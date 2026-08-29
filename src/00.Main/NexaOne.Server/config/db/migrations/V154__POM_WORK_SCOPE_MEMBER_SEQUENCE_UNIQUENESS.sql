-- Owner: POM. 작업 범위 구성원 순번의 동시성 중복 방지.
-- V152의 MAX(SEQUENCE_NO)+1 할당을 기존 애플리케이션과 호환되게 보호한다.

-- 운영 SQL Server에서는 기존 중복 데이터를 자동 수정하지 않는다. 중복이 있으면
-- 명시적 운영 정합성 조치 후 재실행해야 하며, 그 상태로 인덱스를 만들지 않는다.
-- SQLITE-OMIT-BEGIN
IF EXISTS (
    SELECT 1
      FROM POM_WORK_SCOPE_MEMBER
     GROUP BY WORK_SCOPE_ID, SEQUENCE_NO
    HAVING COUNT_BIG(*) > 1)
    THROW 51523, 'V154 duplicate work-scope member sequence; reconcile before migration', 1;
-- SQLITE-OMIT-END

CREATE UNIQUE INDEX UX_POM_WORK_SCOPE_MEMBER_SEQUENCE
    ON POM_WORK_SCOPE_MEMBER (WORK_SCOPE_ID, SEQUENCE_NO);
