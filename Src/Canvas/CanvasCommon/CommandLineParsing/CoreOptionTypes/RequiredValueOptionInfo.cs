using System.Linq;
using CanvasCommon.CommandLineParsing.OptionProcessing;

namespace CanvasCommon.CommandLineParsing.CoreOptionTypes
{
    public class RequiredValueOptionInfo : ValueOptionInfo<string>
    {
        public RequiredValueOptionInfo(string description, params string[] names) : base(true, description, names)
        {
        }

        public RequiredValueOptionInfo(IOptionInfo optionInfo) : this(optionInfo.Description, optionInfo.Names.ToArray())
        { }

        public override IParsingResult<string> Parse(SuccessfulResultCollection parseInput)
        {
            string value = parseInput.Get(this);
            if (value == null)
                return ParsingResult<string>.FailedResult($"Error: {Name} is a required option");
            return ParsingResult<string>.SuccessfulResult(value);
        }
    }
}