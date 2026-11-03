using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// One interactable card pedestal in the shop. Interact forwards the card index
/// to the ShopController (which owns the per-player card state).
/// </summary>
public class ShopCardPick : UdonSharpBehaviour
{
    public ShopController shop;
    public int cardIndex;

    public override void Interact()
    {
        if (shop != null) shop._PickCard(cardIndex);
    }
}
