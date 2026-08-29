// TS↔C# 화면정의 계약 드리프트 가드 — 공유 픽스처(test/contract/screen-definition-contract.json)와
// layout.ts 키 상수(SCREEN_DEFINITION_KEYS — DTO 타입 완전성 체크 내장)를 대조한다.
// C#만 속성을 추가하면(픽스처 갱신 강제됨) 이 테스트가 실패해 SPA 미러 누락을 잡는다
// (DeleteQueryId 미러 누락 실사고의 재발 방지 가드).
import { readFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import {
  BULK_COMMAND_KEYS, BUTTON_WIDGET_KEYS, COLLECTION_WIDGET_KEYS, FIELD_DEFINITION_KEYS, FIELD_VALUE_GENERATOR_VALUES,
  FORM_WIDGET_KEYS, GRID_WIDGET_KEYS, LAYOUT_KIND_VALUES,
  SCREEN_DEFINITION_KEYS, SCREEN_PURPOSE_VALUES,
} from '../layout'

const here = dirname(fileURLToPath(import.meta.url))
// __tests__ → designer → src → Portal → ClientApps → NexaOne.Server → 00.Main → src → (리포 루트)
const fixturePath = resolve(here, '../../../../../../../../test/contract/screen-definition-contract.json')

interface ContractFixture {
  screenDefinition: string[]
  bulkCommandDefinition: string[]
  fieldDefinition: string[]
  gridWidget: string[]
  formWidget: string[]
  collectionWidget: string[]
  buttonWidget: string[]
  fieldValueGenerator: string[]
  layoutKinds: string[]
  screenPurpose: string[]
}
const fixture = JSON.parse(readFileSync(fixturePath, 'utf-8')) as ContractFixture

// C#은 PascalCase, TS는 camelCase — 첫 글자만 정규화해 비교한다(계약상 이름은 1:1).
const toCamel = (s: string) => s.charAt(0).toLowerCase() + s.slice(1)

describe('TS↔C# 화면정의 계약 드리프트 가드', () => {
  it('ScreenDefinitionDto 키가 공유 픽스처와 일치한다', () => {
    const expected = [...fixture.screenDefinition.map(toCamel)].sort()
    const actual = [...SCREEN_DEFINITION_KEYS].sort()
    expect(actual, 'C#(ScreenDefinition)과 SPA(layout.ts) 계약이 어긋남 — 픽스처 _comment의 6단계 절차로 동기화').toEqual(expected)
  })

  it('BulkCommandDefinition 키가 공유 픽스처와 일치한다', () => {
    const expected = [...fixture.bulkCommandDefinition.map(toCamel)].sort()
    const actual = [...BULK_COMMAND_KEYS].sort()
    expect(actual).toEqual(expected)
  })

  it('FieldDefinition 키와 값 생성 enum이 공유 픽스처와 일치한다', () => {
    expect([...FIELD_DEFINITION_KEYS].sort()).toEqual(fixture.fieldDefinition.map(toCamel).sort())
    expect([...FIELD_VALUE_GENERATOR_VALUES]).toEqual(fixture.fieldValueGenerator)
  })

  it('Grid/Form/Button scope 계약이 공유 픽스처와 일치한다', () => {
    expect([...GRID_WIDGET_KEYS].sort()).toEqual(fixture.gridWidget.map(toCamel).sort())
    expect([...FORM_WIDGET_KEYS].sort()).toEqual(fixture.formWidget.map(toCamel).sort())
    expect([...COLLECTION_WIDGET_KEYS].sort()).toEqual(fixture.collectionWidget.map(toCamel).sort())
    expect([...BUTTON_WIDGET_KEYS].sort()).toEqual(fixture.buttonWidget.map(toCamel).sort())
  })

  it('LayoutNode discriminator가 collection을 포함한 공유 픽스처와 일치한다', () => {
    expect([...LAYOUT_KIND_VALUES]).toEqual(fixture.layoutKinds)
  })

  it('ScreenPurpose 값과 순서가 공유 픽스처와 일치한다', () => {
    expect([...SCREEN_PURPOSE_VALUES]).toEqual(fixture.screenPurpose)
  })
})
