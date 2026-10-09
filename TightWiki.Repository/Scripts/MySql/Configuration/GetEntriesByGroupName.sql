SELECT
    CE.Id,
    CE.ConfigurationGroupId,
    CE.Name,
    CE.Value,
    CE.IsEncrypted,
    CE.Description
FROM ConfigurationEntry CE
INNER JOIN ConfigurationGroup CG ON CG.Id = CE.ConfigurationGroupId
WHERE CG.Name = @GroupName
ORDER BY CE.Name;
