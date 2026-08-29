-- Equipment operators need read-only access to released recipe context while write/approval
-- permissions remain explicitly assigned. Only known standard-role payloads
-- are upgraded; administrator-customized OPERATOR grants are preserved.
UPDATE SYS_ROLE
SET PERMISSIONS = 'fdc:control|fdc:read|mdm:read|est:read|pom:read|pom:execute|pom:routing.request|rms:read',
    UPDATED_BY = 'SYSTEM',
    UPDATED_AT = GETUTCDATE()
WHERE ROLE_ID = 'OPERATOR'
  AND IS_DELETED = 0
  AND PERMISSIONS IN (
      '',
      'fdc:control|fdc:read',
      'fdc:control|fdc:read|pom:execute',
      'fdc:control|fdc:read|mdm:read|est:read|pom:read|pom:execute',
      'fdc:control|fdc:read|mdm:read|est:read|pom:read|pom:execute|pom:routing.request'
  );

-- Manual PM/BM execution needs its own least-privilege role instead of granting all EMS data and
-- write operations to every equipment operator.
INSERT INTO SYS_ROLE
    (ROLE_ID, ROLE_NAME, DESCRIPTION, PERMISSIONS, IS_DELETED,
     CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
SELECT
    'MAINTENANCE', 'Maintenance', 'Equipment maintenance worker role',
    'fdc:read|mdm:read|ems:read|ems:manage|est:read|pom:read|rms:read', 0,
    'SYSTEM', GETUTCDATE(), 'SYSTEM', GETUTCDATE()
WHERE NOT EXISTS (
    SELECT 1 FROM SYS_ROLE WHERE ROLE_ID = 'MAINTENANCE'
);
