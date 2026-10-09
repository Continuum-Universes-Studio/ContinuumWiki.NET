using Dapper;
using System.Data;

namespace TightWiki.Library
{
    public class GuidTypeHandler
        : SqlMapper.TypeHandler<Guid>
    {
        public override void SetValue(IDbDataParameter parameter, Guid value)
        {
            parameter.Value = value.ToString();
        }

        public override Guid Parse(object value)
        {
            return value is Guid guid ? guid : Guid.Parse(value.ToString()!);
        }
    }
}
