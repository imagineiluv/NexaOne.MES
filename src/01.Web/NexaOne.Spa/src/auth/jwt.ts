// JWT permission 클레임 디코딩(Phase 2 — ADR-003 PEP와 동일 권한 모델로 UI 가드).
// 클라이언트 디코딩은 UI 표시 제어용일 뿐, 실제 인가는 서버(PermissionPolicyProvider)가 강제한다.

function decodeJwtClaims(token: string): Record<string, unknown> {
  const part = token.split('.')[1]
  if (!part) return {}
  try {
    const base64 = part.replace(/-/g, '+').replace(/_/g, '/')
    return JSON.parse(atob(base64)) as Record<string, unknown>
  } catch {
    return {}
  }
}

function permissionsFromToken(token: string | null): string[] {
  if (!token) return []
  const claim = decodeJwtClaims(token)['permission']
  if (Array.isArray(claim)) return claim.filter((x): x is string => typeof x === 'string')
  return typeof claim === 'string' ? [claim] : []
}

// 서버 핸들러와 동일 규칙: 전체 권한("*") 보유 또는 해당 권한 보유 시 true.
export function hasPermission(token: string | null, permission: string): boolean {
  const perms = permissionsFromToken(token)
  return perms.includes('*') || perms.includes(permission)
}
