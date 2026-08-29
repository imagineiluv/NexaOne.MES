-- Named read queries are deny-by-default from V096. Keep standard role rows aligned with the
-- operator bootstrap contract so normal OPERATOR sessions retain their existing screens.
-- Only untouched legacy defaults are replaced; customized standard roles remain authoritative
-- and administrators grant any additional read permissions explicitly in SYS_ROLE.PERMISSIONS.
UPDATE SYS_ROLE
SET PERMISSIONS = 'fdc:control|fdc:read|mdm:read|est:read|pom:read|pom:execute',
    UPDATED_BY = 'SYSTEM',
    UPDATED_AT = GETUTCDATE()
WHERE ROLE_ID = 'OPERATOR'
  AND IS_DELETED = 0
  AND PERMISSIONS IN ('', 'fdc:control|fdc:read', 'fdc:control|fdc:read|pom:execute');

-- VIEWER intentionally remains fdc:read only. Other module reads must be explicitly granted by an administrator.
