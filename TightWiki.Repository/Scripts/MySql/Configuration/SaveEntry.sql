INSERT INTO ConfigurationEntry
    (ConfigurationGroupId, Name, Value, DataTypeId, Description, IsEncrypted, IsRequired)
SELECT Id, @EntryName, @Value, 0, NULL, FALSE, FALSE
FROM ConfigurationGroup
WHERE Name = @GroupName
ON DUPLICATE KEY UPDATE Value = VALUES(Value);
