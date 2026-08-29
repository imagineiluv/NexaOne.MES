/**
 * 관리 화면의 디자인 검토에만 사용하는 Portal 전용 보기 모드다.
 *
 * 이 값은 ScreenDefinition 계약이나 저장 payload에 포함하지 않는다. 실제 런타임의
 * 표 밀도 같은 개인 설정은 MES 화면에서 사용자별로 관리한다.
 */
export const MANAGE_PREVIEW_MODES = [
  {
    value: 'standard',
    label: '표준 표',
    description: '검색·편집에 균형을 둔 기본 행 간격으로 확인합니다.',
  },
  {
    value: 'dense',
    label: '밀집 표',
    description: '한 화면에 더 많은 행이 보이는 고밀도 표를 확인합니다.',
  },
  {
    value: 'cards',
    label: '카드',
    description: '레코드를 구분된 카드 목록으로 표현한 구성을 확인합니다.',
  },
  {
    value: 'split',
    label: '분할 상세',
    description: '왼쪽 목록과 오른쪽 선택 항목 상세 구성을 함께 확인합니다.',
  },
] as const

export type ManagePreviewMode = (typeof MANAGE_PREVIEW_MODES)[number]['value']

export const DEFAULT_MANAGE_PREVIEW_MODE: ManagePreviewMode = 'standard'

export function describeManagePreviewMode(mode: ManagePreviewMode): string {
  return MANAGE_PREVIEW_MODES.find(item => item.value === mode)?.description ?? ''
}
