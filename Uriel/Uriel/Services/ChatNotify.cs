using ProjectM;
using ProjectM.Network;
using Unity.Collections;
using Unity.Entities;

namespace Uriel.Services;

/// <summary>Send a system chat message to a specific player (KindredCommands pattern).</summary>
internal static class ChatNotify
{
    /// <summary>Send to the user entity (the User-component holder, not the character).</summary>
    public static void ToUserEntity(Entity userEntity, string message)
    {
        if (!userEntity.TryGetComponent<User>(out var user)) return;
        var fixedMsg = new FixedString512Bytes(message);
        ServerChatUtils.SendSystemMessageToClient(Core.EntityManager, user, ref fixedMsg);
    }

    /// <summary>Send to a player character entity (resolves its UserEntity).</summary>
    public static void ToCharacter(Entity characterEntity, string message)
    {
        if (!characterEntity.TryGetComponent<PlayerCharacter>(out var pc)) return;
        ToUserEntity(pc.UserEntity, message);
    }
}
