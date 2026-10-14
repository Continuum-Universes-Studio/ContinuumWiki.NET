"""Mock-only coverage for stopped-source diagnostic authorization and runner."""
from __future__ import annotations
import importlib.util, json, os, subprocess, tempfile, unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path); assert spec and spec.loader
    mod=importlib.util.module_from_spec(spec); spec.loader.exec_module(mod); return mod
PF=load("stopped_diag_pf_tests",ROOT/"scripts/preflight-m5-026b-stopped-source-diagnostic.py")
RUNNER=load("stopped_diag_runner_tests",ROOT/"scripts/cw-m5-026b/diagnose_stopped_source.py")
PLAN=RUNNER.PLAN
NOW=datetime(2026,10,13,1,0,tzinfo=timezone.utc)

def observations():
    t=PLAN.EXPECTED
    fields=[t["ContainerId"],"exited","false","137",t["ImageReference"],"sha256:image",t["ProvisioningRunId"],t["DatabaseName"],f"{t['VolumeName']}=/var/lib/mysql;",f"3306/tcp={t['Endpoint']},;"]
    return {
      "Rootless":{"ReturnCode":0,"Stdout":"true\n"}, "Exists":{"ReturnCode":0,"Stdout":""},
      "Container":{"ReturnCode":0,"Stdout":"|".join(fields)+"\n"},
      "Port":{"ReturnCode":0,"Stdout":f"3306/tcp -> {t['Endpoint']}\n"},
      "Image":{"ReturnCode":0,"Stdout":f"sha256:image|{t['ImageReference']}\n"},
      "Volume":{"ReturnCode":0,"Stdout":f"{t['VolumeName']}|{t['ProvisioningRunId']}|source\n"}}

class StoppedSourceRunnerTests(unittest.TestCase):
  def setUp(self):
    self.temp=tempfile.TemporaryDirectory(); self.base=Path(self.temp.name); os.chmod(self.base,0o700)
    self.manifest_dir=self.base/"manifest"; self.manifest_dir.mkdir(mode=0o700)
    self.out_dir=self.base/"continuumwiki-validation"; self.out_dir.mkdir(mode=0o700)
    PF.VALIDATION_ROOT=self.base; RUNNER.PREFLIGHT.VALIDATION_ROOT=self.base; RUNNER.REPORT_ROOT=self.base
    self.host_patch=patch.object(RUNNER,"verify_execution_host",return_value=None); self.host_patch.start()
    self.manifest_path=self.manifest_dir/"authorization.json"; self.preflight_path=self.out_dir/"preflight.json"; self.result_path=self.out_dir/"result.json"
    target=PLAN.EXPECTED
    self.manifest={"ManifestVersion":PF.CONTRACT,"ApprovalId":"CW-M5-026B-DIAGNOSTIC-TEST-001",
      "ApprovedAtUtc":"2026-10-13T00:30:00Z","ExpiresAtUtc":"2026-10-13T01:30:00Z",
      "Approver":{"Name":"Test Approver","Role":"Project Owner"},"Operator":{"Name":"Test Operator","Role":"System Administrator"},"TargetOwner":"Test Owner",
      "ExecutionHost":{"HostName":"christian-laptop","UnixUser":"cdjensen94","Uid":1000,"RootlessPodman":True},
      "Target":{k:target[k] for k in ("ContainerName","ContainerId","ImageReference","VolumeName","DatabaseName","Endpoint","ProvisioningRunId")},
      "PermittedOperations":PF.OPS,"ResultReportPath":str(self.result_path)}
    self.write(self.manifest_path,self.manifest)
  def tearDown(self): self.host_patch.stop(); self.temp.cleanup()
  @staticmethod
  def write(path,value):
    path.write_text(json.dumps(value),encoding="utf-8"); os.chmod(path,0o600)
  def preflight(self):
    ok,report=PF.run(self.manifest_path,self.preflight_path,now=NOW); self.assertTrue(ok,report); return report
  def bind_fresh_paths(self, result_path, suffix):
    manifest=json.loads(json.dumps(self.manifest)); manifest["ResultReportPath"]=str(result_path)
    mpath=self.manifest_dir/f"authorization-{suffix}.json"; self.write(mpath,manifest)
    pfpath=self.out_dir/f"preflight-{suffix}.json"
    ok,report=PF.run(mpath,pfpath,now=NOW); self.assertTrue(ok,report)
    return mpath,pfpath
  def fake_run(self,values):
    cmds=PLAN.podman_diagnostic_commands()
    reverse={name:values[name] for name in values}
    def run(command,**kwargs):
      self.assertIn(command,cmds)
      index=cmds.index(command); key=("Rootless","Exists","Container","Port","Image","Volume")[index]
      item=reverse[key]
      self.assertIs(kwargs.get("shell"),False)
      self.assertNotIn("CONTAINER_HOST",kwargs.get("env",{}))
      return subprocess.CompletedProcess(command,item["ReturnCode"],item["Stdout"],"private stderr must not persist")
    return run

  def test_diagnostic_contract_is_draft_2020_and_restart_permission_is_false(self):
    try: from jsonschema import Draft202012Validator
    except ImportError: self.skipTest("jsonschema unavailable")
    schema=json.loads((ROOT/"docs/operations/cw-m5-026b-stopped-source-diagnostic-authorization.schema.json").read_text())
    Draft202012Validator.check_schema(schema)
    candidate=dict(self.manifest); candidate["ResultReportPath"]="/home/cdjensen94/continuumwiki-validation/result.json"
    Draft202012Validator(schema).validate(candidate)
    self.assertFalse(PF.OPS["ContainerStart"]); self.assertFalse(PF.OPS["ContainerRestart"])

  def test_preflight_is_nonconnecting_and_rejects_old_inspection_scope(self):
    ok,report=PF.run(self.manifest_path,self.preflight_path,now=NOW)
    self.assertTrue(ok); self.assertEqual("CATEGORY_STOPPED_SOURCE_DIAGNOSTIC_AUTHORIZATION_VALID_NONCONNECTING",report["Classification"])
    self.assertEqual({"Podman":0,"DatabaseConnections":0,"DatabaseMutations":0,"CredentialReads":0},report["ResourceOperationCounts"])
    bad=dict(self.manifest); bad["PermittedOperations"]={"ConnectSourceReadOnly":True}
    self.assertRaises(PF.PreflightError,PF.validate,bad,NOW)

  def test_preflight_rejects_expiry_and_target_drift(self):
    expired=dict(self.manifest); expired["ExpiresAtUtc"]="2026-10-13T00:59:00Z"
    self.assertRaises(PF.PreflightError,PF.validate,expired,NOW)
    changed=json.loads(json.dumps(self.manifest)); changed["Target"]["ContainerId"]="a"*64
    self.assertRaises(PF.PreflightError,PF.validate,changed,NOW)

  def test_preflight_rejects_manifest_symlink_and_never_overwrites_report(self):
    alias=self.manifest_dir/"authorization-link.json"; alias.symlink_to(self.manifest_path)
    report=self.out_dir/"symlink-preflight.json"
    ok,data=PF.run(alias,report,now=NOW)
    self.assertFalse(ok); self.assertIn("MANIFEST_FILE_PERMISSIONS_INVALID",data["Errors"])
    self.assertRaises(PF.PreflightError,PF.run,self.manifest_path,report,NOW)

  def test_execution_host_mismatch_fails_before_any_podman_call(self):
    m,p=self.bind_fresh_paths(self.result_path,"host-check")
    self.host_patch.stop()
    with patch.object(RUNNER.socket,"gethostname",return_value="other-host"), patch.object(RUNNER.os,"getuid",return_value=1000), patch.object(RUNNER.getpass,"getuser",return_value="cdjensen94"):
      with self.assertRaisesRegex(RUNNER.DiagnosticError,"AUTHORIZED_HOST_IDENTITY_MISMATCH"):
        RUNNER.validate_execution(m,p,self.result_path,NOW)

  def test_runner_success_uses_six_allowlisted_reads_and_sanitizes_output(self):
    mpath,pfpath=self.bind_fresh_paths(self.result_path,"success")
    report=RUNNER.execute(mpath,pfpath,self.result_path,self.fake_run(observations()),now=NOW)
    self.assertEqual("SOURCE_CONTAINER_STOPPED_IDENTITY_MATCH",report["Classification"])
    self.assertEqual(6,report["PodmanReadOnlyCommands"]); self.assertEqual(0,report["DatabaseConnections"])
    saved=json.loads(self.result_path.read_text()); self.assertNotIn("private stderr must not persist",json.dumps(saved)); self.assertNotIn("Stdout",saved)
    self.assertEqual(0o600,self.result_path.stat().st_mode & 0o777)
    try: from jsonschema import Draft202012Validator
    except ImportError: self.skipTest("jsonschema unavailable")
    schema=json.loads((ROOT/"docs/operations/cw-m5-026b-stopped-source-diagnostic-report.schema.json").read_text())
    Draft202012Validator.check_schema(schema); Draft202012Validator(schema).validate(report)
    self.assertRaises(RUNNER.DiagnosticError,RUNNER.execute,mpath,pfpath,self.result_path,self.fake_run(observations()),NOW)

  def test_consumed_authorization_cannot_be_replayed_even_if_result_is_removed(self):
    m,p=self.bind_fresh_paths(self.result_path,"consume")
    RUNNER.execute(m,p,self.result_path,self.fake_run(observations()),now=NOW)
    self.result_path.unlink()  # isolated temporary test fixture only
    calls=[]
    def should_not_run(command,**kwargs): calls.append(command); raise AssertionError("runner should reject before Podman")
    with self.assertRaisesRegex(RUNNER.DiagnosticError,"AUTHORIZATION_ATTEMPT_ALREADY_CLAIMED"):
      RUNNER.execute(m,p,self.result_path,should_not_run,now=NOW)
    self.assertEqual([],calls)

  def test_every_rootless_and_exists_return_code_is_fail_closed_or_classified(self):
    for rc in (1,2,125,126):
      with self.subTest(rootless_rc=rc):
        v=observations(); v["Rootless"]={"ReturnCode":rc,"Stdout":""}
        output=self.out_dir/f"rootless-{rc}.json"; m,p=self.bind_fresh_paths(output,f"rootless-{rc}")
        result=RUNNER.execute(m,p,output,self.fake_run(v),NOW)
        self.assertEqual("DIAGNOSTIC_INCOMPLETE",result["Classification"])
        self.assertEqual(1,result["PodmanReadOnlyCommands"])
    for rc,classification in ((1,"SOURCE_CONTAINER_MISSING"),(125,"SOURCE_CONTAINER_STORAGE_INACCESSIBLE"),(2,"DIAGNOSTIC_INCOMPLETE"),(126,"DIAGNOSTIC_INCOMPLETE")):
      with self.subTest(exists_rc=rc):
        v=observations(); v["Exists"]={"ReturnCode":rc,"Stdout":""}
        output=self.out_dir/f"exists-{rc}.json"; m,p=self.bind_fresh_paths(output,f"exists-{rc}")
        result=RUNNER.execute(m,p,output,self.fake_run(v),NOW)
        self.assertEqual(classification,result["Classification"]); self.assertEqual(2,result["PodmanReadOnlyCommands"])

  def test_every_inspection_command_failure_and_identity_mismatch_is_reported(self):
    for key in ("Container","Image","Volume"):
      v=observations(); v[key]={"ReturnCode":125,"Stdout":""}
      output=self.out_dir/f"failed-{key}.json"; m,p=self.bind_fresh_paths(output,f"failed-{key}")
      report=RUNNER.execute(m,p,output,self.fake_run(v),NOW)
      self.assertEqual("DIAGNOSTIC_INCOMPLETE",report["Classification"])
      self.assertNotIn("private stderr",json.dumps(report))
    v=observations(); v["Port"]={"ReturnCode":125,"Stdout":""}
    output=self.out_dir/"failed-stopped-Port.json"; m,p=self.bind_fresh_paths(output,"failed-stopped-Port")
    report=RUNNER.execute(m,p,output,self.fake_run(v),NOW)
    self.assertEqual("SOURCE_CONTAINER_STOPPED_IDENTITY_MATCH",report["Classification"])
    self.assertIn("PODMAN_PORT_OBSERVATION_UNAVAILABLE",report["Findings"])
    mutations=(("Container",0,"b"*64,"CONTAINER_ID_MISMATCH"),("Container",6,"wrong-run","PROVISIONING_RUN_ID_LABEL_MISMATCH"),
      ("Container",7,"wrong-db","DATABASE_LABEL_MISMATCH"),("Container",8,"wrong=/var/lib/mysql;","VOLUME_MOUNT_MISMATCH"),
      ("Container",9,"3306/tcp=0.0.0.0:13326,;","CONFIGURED_ENDPOINT_MISMATCH"),
      ("Image",None,"sha256:other|mariadb@sha256:"+"0"*64,"IMMUTABLE_IMAGE_ID_OR_DIGEST_MISMATCH"),
      ("Volume",None,"wrong|wrong-run|source","VOLUME_IDENTITY_MISMATCH"))
    for ix,(key,index,text,code) in enumerate(mutations):
      with self.subTest(identity=code):
        v=observations(); old=v[key]["Stdout"]
        if index is None: v[key]["Stdout"]=text
        else:
          f=old.strip().split("|"); f[index]=text; v[key]["Stdout"]="|".join(f)
        output=self.out_dir/f"identity-{ix}.json"; m,p=self.bind_fresh_paths(output,f"identity-{ix}")
        report=RUNNER.execute(m,p,output,self.fake_run(v),NOW)
        self.assertEqual("SOURCE_CONTAINER_RECREATED" if code=="CONTAINER_ID_MISMATCH" else "SOURCE_CONTAINER_IDENTITY_MISMATCH",report["Classification"])
        self.assertIn(code,report["Findings"])

  def test_preflight_report_binding_freshness_and_existing_outputs_are_enforced(self):
    m,p=self.bind_fresh_paths(self.result_path,"tamper"); altered=json.loads(p.read_text()); altered["ApprovalId"]="CW-M5-026B-DIAGNOSTIC-OTHER"; self.write(p,altered)
    self.assertRaises(RUNNER.DiagnosticError,RUNNER.validate_execution,m,p,self.result_path,NOW)

if __name__=="__main__": unittest.main()
