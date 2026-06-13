using BrokerCore.Services;

namespace Unit.Tests.Services;

/// <summary>
/// SchemaValidator(Phase 1 簡易 JSON Schema 驗證器)純邏輯測試:
/// 空 schema 通行、非法 JSON 錯誤路徑、type / required / properties 遞迴 /
/// items / maxLength / enum 各分支與邊界(integer vs number、巢狀路徑回報)。
/// </summary>
public class SchemaValidatorTests
{
    private readonly SchemaValidator _sut = new();

    // --- 空 / 無限制 schema 直接通過 ---

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("  {}  ")]
    public void EmptySchema_AlwaysValid(string schema)
    {
        // 即使 payload 是垃圾,空 schema 也短路為通過(根本沒解析 payload)
        var (isValid, error) = _sut.Validate("not-even-json", schema);
        isValid.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void NullSchema_Valid()
    {
        var (isValid, error) = _sut.Validate("123", null!);
        isValid.Should().BeTrue();
        error.Should().BeNull();
    }

    // --- 非法 JSON 錯誤路徑 ---

    [Fact]
    public void InvalidJsonPayload_ReturnsError()
    {
        var (isValid, error) = _sut.Validate("{not valid", """{"type":"object"}""");
        isValid.Should().BeFalse();
        error.Should().StartWith("Invalid JSON payload");
    }

    [Fact]
    public void InvalidJsonSchema_ReturnsError()
    {
        // payload 合法、但 schema 本身解析失敗 → 報 schema 錯誤
        var (isValid, error) = _sut.Validate("123", "{bad schema");
        isValid.Should().BeFalse();
        error.Should().StartWith("Invalid JSON Schema");
    }

    // --- type 檢查 ---

    [Theory]
    [InlineData("object", "{}")]
    [InlineData("array", "[]")]
    [InlineData("string", "\"hi\"")]
    [InlineData("number", "1.5")]
    [InlineData("integer", "42")]
    [InlineData("boolean", "true")]
    [InlineData("boolean", "false")]
    [InlineData("null", "null")]
    public void TypeMatch_Passes(string type, string payload)
    {
        var (isValid, _) = _sut.Validate(payload, $$"""{"type":"{{type}}"}""");
        isValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("string", "123")]
    [InlineData("number", "\"x\"")]
    [InlineData("object", "[]")]
    [InlineData("array", "{}")]
    [InlineData("boolean", "1")]
    [InlineData("null", "0")]
    public void TypeMismatch_ReturnsError(string type, string payload)
    {
        var (isValid, error) = _sut.Validate(payload, $$"""{"type":"{{type}}"}""");
        isValid.Should().BeFalse();
        error.Should().Contain("Type mismatch").And.Contain(type);
    }

    [Fact]
    public void IntegerType_RejectsNonIntegralNumber()
    {
        // 1.5 是 number 但不是 integer → integer 分支要求 TryGetInt64 成功
        var (isValid, error) = _sut.Validate("1.5", """{"type":"integer"}""");
        isValid.Should().BeFalse();
        error.Should().Contain("integer");
    }

    [Fact]
    public void NumberType_AcceptsIntegralValue()
    {
        // number 接受整數字面量(JsonValueKind.Number)
        var (isValid, _) = _sut.Validate("42", """{"type":"number"}""");
        isValid.Should().BeTrue();
    }

    [Fact]
    public void UnknownType_IsUnconstrained()
    {
        // 未知型別 → MatchesType 落 default(true)、不限制
        var (isValid, _) = _sut.Validate("\"anything\"", """{"type":"weirdtype"}""");
        isValid.Should().BeTrue();
    }

    // --- required 檢查 ---

    [Fact]
    public void Required_AllPresent_Valid()
    {
        var schema = """{"type":"object","required":["path","mode"]}""";
        var (isValid, _) = _sut.Validate("""{"path":"a","mode":"r"}""", schema);
        isValid.Should().BeTrue();
    }

    [Fact]
    public void Required_MissingField_ReturnsError()
    {
        var schema = """{"type":"object","required":["path","mode"]}""";
        var (isValid, error) = _sut.Validate("""{"path":"a"}""", schema);
        isValid.Should().BeFalse();
        error.Should().Contain("Missing required field").And.Contain("mode");
    }

    [Fact]
    public void Required_IgnoredWhenValueNotObject()
    {
        // required 僅對 object 生效;value 是 array → required 不檢查
        var schema = """{"required":["path"]}""";
        var (isValid, _) = _sut.Validate("[]", schema);
        isValid.Should().BeTrue();
    }

    // --- properties 遞迴驗證 ---

    [Fact]
    public void Properties_RecursivelyValidatesPresentField()
    {
        // path 必須是 string;給 number → 失敗,且錯誤路徑帶子欄位名
        var schema = """{"type":"object","properties":{"path":{"type":"string"}}}""";
        var (isValid, error) = _sut.Validate("""{"path":123}""", schema);
        isValid.Should().BeFalse();
        error.Should().Contain("$.path");
    }

    [Fact]
    public void Properties_AbsentFieldIsNotValidated()
    {
        // properties 只驗「存在」的欄位;path 不在 payload 就不碰(由 required 管必填)
        var schema = """{"type":"object","properties":{"path":{"type":"string"}}}""";
        var (isValid, _) = _sut.Validate("""{"other":1}""", schema);
        isValid.Should().BeTrue();
    }

    [Fact]
    public void Properties_NestedObject_PathReported()
    {
        var schema = """
        {"type":"object","properties":{"opts":{"type":"object","properties":{"depth":{"type":"integer"}}}}}
        """;
        var (isValid, error) = _sut.Validate("""{"opts":{"depth":"x"}}""", schema);
        isValid.Should().BeFalse();
        error.Should().Contain("$.opts.depth");
    }

    // --- items(array)驗證 ---

    [Fact]
    public void Items_AllElementsValid_Passes()
    {
        var schema = """{"type":"array","items":{"type":"string"}}""";
        var (isValid, _) = _sut.Validate("""["a","b","c"]""", schema);
        isValid.Should().BeTrue();
    }

    [Fact]
    public void Items_BadElement_ReportsIndexInPath()
    {
        var schema = """{"type":"array","items":{"type":"string"}}""";
        var (isValid, error) = _sut.Validate("""["a",2,"c"]""", schema);
        isValid.Should().BeFalse();
        error.Should().Contain("$[1]");
    }

    // --- maxLength(string)檢查 ---

    [Fact]
    public void MaxLength_WithinLimit_Valid()
    {
        var schema = """{"type":"string","maxLength":5}""";
        var (isValid, _) = _sut.Validate("\"abcde\"", schema);
        isValid.Should().BeTrue();
    }

    [Fact]
    public void MaxLength_Exceeded_ReturnsError()
    {
        var schema = """{"type":"string","maxLength":3}""";
        var (isValid, error) = _sut.Validate("\"abcd\"", schema);
        isValid.Should().BeFalse();
        error.Should().Contain("String too long");
    }

    // --- enum 檢查 ---

    [Fact]
    public void Enum_AllowedValue_Valid()
    {
        var schema = """{"enum":["read","write","list"]}""";
        var (isValid, _) = _sut.Validate("\"write\"", schema);
        isValid.Should().BeTrue();
    }

    [Fact]
    public void Enum_DisallowedValue_ReturnsError()
    {
        var schema = """{"enum":["read","write"]}""";
        var (isValid, error) = _sut.Validate("\"delete\"", schema);
        isValid.Should().BeFalse();
        error.Should().Contain("enum");
    }

    // --- 綜合:Phase 1 file.read 風格 schema ---

    [Fact]
    public void RealisticFileReadSchema_FullyValidPayload_Passes()
    {
        var schema = """
        {
          "type":"object",
          "required":["path"],
          "properties":{
            "path":{"type":"string","maxLength":260},
            "mode":{"enum":["read","head"]}
          }
        }
        """;
        var payload = """{"path":"docs/readme.md","mode":"read"}""";
        var (isValid, error) = _sut.Validate(payload, schema);
        isValid.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void RealisticFileReadSchema_BadModeEnum_Fails()
    {
        var schema = """
        {
          "type":"object",
          "required":["path"],
          "properties":{
            "path":{"type":"string"},
            "mode":{"enum":["read","head"]}
          }
        }
        """;
        var payload = """{"path":"x","mode":"delete"}""";
        var (isValid, error) = _sut.Validate(payload, schema);
        isValid.Should().BeFalse();
        error.Should().Contain("$.mode");
    }
}
