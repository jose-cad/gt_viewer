namespace GtViewer.Web.Security;

/// <summary>
/// Lista fechada de tipos auditados exigida pela especificação (Convenções / J10 / Tarefa 16).
/// </summary>
public static class TiposAuditoria
{
    public const string LoginOk = "login ok";
    public const string LoginFalho = "login falho";
    public const string CriarPrimeiroAdministradorCli = "criar primeiro administrador (CLI)";
    public const string HabilitarExpirarContaEmergencia = "habilitar/expirar conta de emergência";
    public const string UsoContaEmergencia = "uso da conta de emergência";
    public const string ReconhecerAlerta = "reconhecer alerta";
    public const string EncerrarSessaoDesativacaoAd = "encerrar sessão por desativação no AD";
    public const string FalhaReconferenciaAd = "falha na reconferência do AD";
    public const string AcessarTela = "acessar tela";
    public const string RevogarDireitoOrfaoTela = "revogar direito órfão de tela";
    public const string Reiniciar = "reiniciar";
    public const string Bloquear = "bloquear";
    public const string Desbloquear = "desbloquear";
    public const string VerificarReaplicarBloqueio = "verificar/reaplicar bloqueio na reconexão";
    public const string CancelarPendencia = "cancelar pendência";
    public const string RevelarSenhaResgate = "revelar senha de resgate";
    public const string TrocarSenhaResgate = "trocar senha de resgate";
    public const string GarantirContaResgate = "garantir conta de resgate";
    public const string RedefinirContaResgate = "redefinir conta de resgate";
    public const string BaixarInstalador = "baixar instalador";
    public const string AprovarRejeitarMaquina = "aprovar/rejeitar máquina";
    public const string CorrigirSerie = "corrigir série";
    public const string UnirRegistros = "unir registros";
    public const string Instalar = "instalar";
    public const string Remover = "remover";
    public const string AlterarCatalogo = "alterar catálogo";
    public const string AlterarGrupo = "alterar grupo";
    public const string ImportarPlanilha = "importar planilha";
    public const string CriarAlterarDesativarTecnico = "criar/alterar/desativar técnico";
    public const string Redefinir2Fa = "redefinir 2FA";
    public const string GerarCodigoAtivacao = "gerar código de ativação";
}
