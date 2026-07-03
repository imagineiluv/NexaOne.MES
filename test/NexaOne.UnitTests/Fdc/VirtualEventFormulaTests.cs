using NexaOne.FDC.Domain;

namespace NexaOne.UnitTests.Fdc;

/// <summary>가상 이벤트 수식 평가기(V067 CONDITION_FORMULA) — 비교/논리(AND·OR)/괄호/우선순위와
/// 실패 경로(값 부재·문법 오류는 Result 실패 — 조용한 false 금지)를 검증한다.</summary>
public sealed class VirtualEventFormulaTests
{
    private static readonly IReadOnlyDictionary<string, decimal> Values =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["TEMP"] = 85m,
            ["PRESSURE"] = 3.5m,
            ["SPEED"] = 1200m,
        };

    [Theory]
    [InlineData("TEMP > 80", true)]
    [InlineData("TEMP > 90", false)]
    [InlineData("TEMP >= 85", true)]
    [InlineData("TEMP <= 85", true)]
    [InlineData("TEMP < 85", false)]
    [InlineData("TEMP == 85", true)]
    [InlineData("TEMP != 85", false)]
    [InlineData("TEMP = 85", true)]                                  // 단일 '='도 동등 비교로 수용
    [InlineData("TEMP > 80 AND PRESSURE > 3", true)]
    [InlineData("TEMP > 80 AND PRESSURE > 4", false)]
    [InlineData("TEMP > 90 OR PRESSURE > 3", true)]
    [InlineData("temp > 80 and pressure > 3", true)]                 // 키워드/식별자 대소문자 무시
    [InlineData("TEMP > 90 OR (PRESSURE > 3 AND SPEED >= 1200)", true)]
    [InlineData("(TEMP > 90 OR PRESSURE > 4) AND SPEED >= 1200", false)]
    [InlineData("TEMP > 90 OR PRESSURE > 4 AND SPEED >= 1200", false)]  // AND가 OR보다 먼저(우선순위)
    [InlineData("TEMP > 80 OR PRESSURE > 4 AND SPEED >= 9999", true)]   // OR(true, AND(false)) = true
    [InlineData("SPEED != -1", true)]                                // 음수 리터럴
    public void Evaluates_comparisons_logic_and_precedence(string formula, bool expected)
    {
        var result = VirtualEventFormula.Evaluate(formula, Values);
        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Description : "");
        result.Value.Should().Be(expected, formula);
    }

    [Theory]
    [InlineData("HUMIDITY > 50", "HUMIDITY")]     // 값 없는 파라미터 — 조용한 false 금지, 실패 보고
    [InlineData("TEMP >", "피연산자")]             // 중간에 끝난 수식
    [InlineData("TEMP 80", "연산자")]              // 비교 연산자 누락
    [InlineData("(TEMP > 80", ")")]               // 괄호 미closure
    [InlineData("TEMP > 80 XOR PRESSURE > 3", "XOR")]  // 미지원 논리 연산자 → 잔여 토큰
    [InlineData("", "비어")]
    public void Invalid_formula_or_missing_value_fails_loudly(string formula, string messagePart)
    {
        var result = VirtualEventFormula.Evaluate(formula, Values);
        result.IsFailure.Should().BeTrue($"'{formula}'는 실패해야 한다");
        result.Error.Description.Should().Contain(messagePart);
    }
}
