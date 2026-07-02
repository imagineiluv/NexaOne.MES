-- SYS Module: 표준 역할 시드(OPERATOR/VIEWER) — 미해결 제품 결정 확정분(SEC-1 후속).
-- V031은 ADMIN('*')만 시드해 register의 활성 역할 검증(SEC-1)이 사실상 ADMIN만 허용했다.
-- RolePermissionDefaults의 기본 매핑을 SYS_ROLE.PERMISSIONS('|' 조인)로 명시 미러링해 DB를 권한의
-- 단일 원천으로 만든다(SEC-2에서 ADMIN 하드코딩 '*' 제거의 전제). 주의: SqliteSchemaInitializer의
-- INSERT 시드는 빈 DB 경로에서만 적용된다 — 기존 dev DB는 재생성(또는 수동 INSERT) 필요.
INSERT INTO SYS_ROLE
    (ROLE_ID, ROLE_NAME, DESCRIPTION, PERMISSIONS, IS_DELETED,
     CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
VALUES
    ('OPERATOR', 'Operator', 'Equipment operator role', 'fdc:control|fdc:read', 0,
     'SYSTEM', GETUTCDATE(), 'SYSTEM', GETUTCDATE());

INSERT INTO SYS_ROLE
    (ROLE_ID, ROLE_NAME, DESCRIPTION, PERMISSIONS, IS_DELETED,
     CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
VALUES
    ('VIEWER', 'Viewer', 'Read-only viewer role', 'fdc:read', 0,
     'SYSTEM', GETUTCDATE(), 'SYSTEM', GETUTCDATE());
