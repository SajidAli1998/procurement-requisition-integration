#!/usr/bin/env python3
"""Generates data/employee-directory.json from the client's workbook (needs: pip install openpyxl).

The workbook's AI_API_Contract_Tests refer to employees but never say who owns which requisition or who may
raise one. This script records our assumption (see data/README.md):
  - employee E10NN raised REQ-70NN and belongs to its department;
  - status reads that expect 403 were raised by a colleague in the same department (E10NN+8);
  - creates that expect 403 come from employees who may read but not raise;
  - E1900 holds the delegated procurement role.

Usage: python3 scripts/generate_employee_directory.py data/<workbook>.xlsx data/employee-directory.json
"""
import openpyxl,sys,json,re
wb=openpyxl.load_workbook(sys.argv[1],data_only=True)
def rows(name):
    it=list(wb[name].iter_rows(values_only=True)); h=it[2]
    return [dict(zip(h,r)) for r in it[3:] if any(r)]
reqs=rows('Approved_Requisitions'); lines=rows('Requisition_Lines'); tests=rows('AI_API_Contract_Tests')
dept={r['Source_Requisition_ID']:r['Requester_Dept'] for r in reqs}
cc={}
for l in lines: cc.setdefault(dept[l['Source_Requisition_ID']],set()).add(l['Cost_Centre'])
assert all(len(v)==1 for v in cc.values()), cc
n=lambda rid:int(rid.split('-')[1])-7000
requester={r:f"E{1000+n(r)}" for r in dept}
no_raise=set(); colleague_owned=[]
for t in tests:
    if t['Expected_HTTP_Status']!=403: continue
    if t['Operation']=='GET status':
        rid=re.search(r'REQ-\d+',t['Request_Payload_Summary']).group(0)
        other=f"REQ-{7000+n(rid)+8}"; assert dept[other]==dept[rid]
        requester[rid]=f"E{1000+n(other)}"; colleague_owned.append(rid)
    else: no_raise.add(t['SignedIn_Employee_ID'])
emps=[]
for i in range(1,121):
    e=f"E{1000+i}"; d=dept[f"REQ-{7000+i}"]
    emps.append({"id":e,"name":f"Test Employee {1000+i}","department":d,"costCentre":next(iter(cc[d])),"canRaise":e not in no_raise,"procurementDelegate":False})
emps.append({"id":"E1900","name":"Test Procurement Delegate","department":"Procurement","costCentre":next(iter(cc['Procurement'])),"canRaise":True,"procurementDelegate":True})
out={"_about":["Synthetic employee directory: an assumption added by the integration team, not part of the client's workbook.",
 "The workbook's AI_API_Contract_Tests refer to employees E1001-E1060 and the rule 'own record or delegated procurement role', but nothing links employees to requisitions or roles.",
 "Assumptions: employee E10NN raised REQ-70NN and belongs to its department; "+", ".join(colleague_owned)+" were raised by a colleague in the same department (E10NN+8), so the tests expecting 403 read someone else's record;",
 "employees "+", ".join(sorted(no_raise))+" may read but not raise requisitions through the assistant; E1900 holds the delegated procurement role and may read every requisition."],
 "employees":emps,"requisitionRequesters":requester}
json.dump(out,open(sys.argv[2],'w'),indent=2); print(colleague_owned,sorted(no_raise))
