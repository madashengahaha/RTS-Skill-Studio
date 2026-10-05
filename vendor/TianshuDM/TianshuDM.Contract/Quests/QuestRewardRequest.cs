namespace TianshuDM.Contract.Quests;

public sealed record QuestRewardRequest(string Type, int ConfigId, int Amount);
