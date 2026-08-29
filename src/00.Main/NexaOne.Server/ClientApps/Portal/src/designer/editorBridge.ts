import type { Editor } from 'grapesjs'
import { componentToLayout } from './mapping'
import type { GrapesNode, LayoutNode } from './layout'

// GrapesJS 에디터 ↔ 스키마 경계. mapping.ts(순수 GrapesNode↔LayoutNode 변환)는 평면 GrapesNode를 가정하므로,
// 실 에디터에서 읽을 때의 형태 차이를 여기서 흡수한다(에디터 의존은 이 모듈에만 둔다).

// 캔버스 루트(보통 단일 nx-section)를 스키마 LayoutNode로 환원. 첫 유효 노드만 채택(잠금 규칙상 루트=섹션 1개).
//
// 읽기경로 무손실 주의: grapesjs 0.21의 Component.toJSON()이 돌려주는 `components`는 평면 배열이 아니라
// Backbone Collection 객체다(Array.isArray=false). componentToLayout이 `(comp.components ?? []).map(...)`로
// 평면 배열을 가정하므로, Collection을 그대로 넘기면 .map이 모델 인스턴스를 순회해 모든 자식이 null로 떨어지고
// 중첩 트리가 통째로 누락된다(저장 시 빈 루트 섹션만 영속되는 데이터 손실). 따라서 JSON 정규화
// (parse(stringify))로 평면 트리로 환원한 뒤 변환한다 — Collection.toJSON()이 배열로 직렬화됨.
export function readRootLayout(editor: Editor): LayoutNode | null {
  for (const c of editor.getComponents().models) {
    const plain = JSON.parse(JSON.stringify(c.toJSON())) as GrapesNode
    const node = componentToLayout(plain)
    if (node) return node
  }
  return null
}
