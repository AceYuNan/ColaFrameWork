---
---                 ColaFramework
--- Copyright © 2018-2049 ColaFramework 马三小伙儿
---                   登录界面
---

local UIBase = require("Core.ui.UIBase")
local UILoginPanel = Class("UILoginPanel",UIBase)

local _instance = nil

function UILoginPanel.Instance()
    if nil == _instance then
        _instance = UILoginPanel:new()
    end
    return _instance
end

function UILoginPanel:InitParam()
    self.uiDepthLayer = ECEnumType.UIDepth.NORMAL
    self:ShowUIMask(true)
end

-- override UI面板创建结束后调用，可以在这里获取gameObject和component等操作
function UILoginPanel:OnCreate()
    Util.UI.SetImageSpriteFromAtlas(self.m_okBtn.image,"bt_buy")
end

-- 界面可见性变化的时候触发
function UILoginPanel:OnShow(isShow)
    Ctrl.Login.RequestConnectServer()
end

function UILoginPanel:onClick(name)
    if name == "cancelBtn" then
        self:DestroySelf()
    elseif name == "okBtn" then
        self:DestroySelf()
        UIManager.Instance():Close(ECEnumType.UIEnum.Loading)
        UIManager.Instance():Open(ECEnumType.UIEnum.WorldDialog)
        SceneCharacter.CreateSceneCharacterInf("Arts/Avatar/Blade_Girl.prefab", AnimCtrlEnum.CharAnimator, true)
        Ctrl.Login.RequestConnectServer()
    end
end

-- 界面销毁的过程中触发
function UILoginPanel:OnDestroy()
    UIBase.OnDestroy(self)
end

return UILoginPanel